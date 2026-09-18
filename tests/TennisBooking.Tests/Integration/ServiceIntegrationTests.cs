using System.Linq.Expressions;
using System.Net;
using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using TennisBooking.Application.Abstractions;
using TennisBooking.Application.Booking;
using TennisBooking.DAL;
using TennisBooking.DAL.Models;
using TennisBooking.Domain.Booking;
using TennisBooking.HealthChecks;
using TennisBooking.Infrastructure.Persistence;
using TennisBooking.Infrastructure.Scheduling;
using TennisBooking.Infrastructure.Telegram;
using TennisBooking.Infrastructure.Weather;
using TennisBooking.Options;
using Xunit;

namespace TennisBooking.Tests.Integration;

/// <summary>
/// Service-level integration tests without containers or live internet:
/// real use cases wired to real EF InMemory repositories plus stubbed
/// HTTP/scheduler boundaries. Covers the repo → use-case → scheduler/HTTP
/// paths the unit suite (pure mocks) and the Postgres suite (docker) miss here.
/// </summary>
public sealed class ServiceIntegrationTests
{
    private static ApplicationDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private static UserConfig SeedConfig(ApplicationDbContext db, string username, DayOfWeek day = DayOfWeek.Monday, int hour = 10)
    {
        var entity = new UserConfig
        {
            Username = username,
            Password = "pw-" + username,
            ResourceId = "res-" + username,
            Venue = "Galaktyka",
            VenueUser = "venue-user",
            DayOfWeek = day,
            Hour = hour
        };
        db.UserConfigs.Add(entity);
        db.SaveChanges();
        return entity;
    }

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTimeOffset utcNow) => UtcNow = utcNow;
        public DateTimeOffset UtcNow { get; }
    }

    private sealed class StubSkedda : ISkeddaClient
    {
        public int BookCalls;
        public int CancelCalls;
        public Func<BookingUserConfig, BookingSlot, PreparedBooking>? PrepareFunc;

        public Task<PreparedBooking> PrepareBookingAsync(BookingUserConfig userConfig, BookingSlot slot, CancellationToken ct)
        {
            if (PrepareFunc is not null)
                return Task.FromResult(PrepareFunc(userConfig, slot));
            return Task.FromResult(new PreparedBooking(
                userConfig, slot, """{"booking":{}}""",
                "X-Skedda-RequestVerificationCookie=c; X-Skedda-ApplicationCookie=a",
                "tok", "c", "a"));
        }

        public Task<SkeddaBookingResult> BookAsync(PreparedBooking booking, int pipeId, CancellationToken ct)
        {
            BookCalls++;
            return Task.FromResult(new SkeddaBookingResult("sk-" + BookCalls));
        }

        public Task<SkeddaWarmupResult> WarmupAsync(PreparedBooking booking, int pipeId, CancellationToken ct)
            => Task.FromResult(new SkeddaWarmupResult(true, null));

        public Task CancelAsync(PreparedBooking booking, string bookingId, CancellationToken ct)
        {
            CancelCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingScheduler : IBookingScheduler
    {
        public readonly List<BookingUserConfig> Recurring = new();
        public readonly List<(int configId, DateTimeOffset start, DateTimeOffset runAt)> Fallbacks = new();
        public readonly List<string> Deleted = new();
        public readonly List<(long chatId, int messageId, string type)> AttendanceChecks = new();
        public int PreciseCount;

        public void SchedulePreciseBooking(PreparedBooking booking) => PreciseCount++;
        public void ScheduleFallback(int userConfigId, DateTimeOffset startTime, DateTimeOffset runAt)
            => Fallbacks.Add((userConfigId, startTime, runAt));
        public string ScheduleAttendanceCheck(long chatId, int telegramMessageId, DateTimeOffset slotStartUtc, string reminderType, DateTimeOffset runAtUtc)
        {
            AttendanceChecks.Add((chatId, telegramMessageId, reminderType));
            return "job-" + reminderType;
        }
        public void DeleteAttendanceCheck(string jobId) => Deleted.Add(jobId);
        public void ScheduleRecurringPreparation(BookingUserConfig userConfig) => Recurring.Add(userConfig);
    }

    private static (ExecuteBookingUseCase execute, StubSkedda skedda, RecordingScheduler scheduler, BookingCancellationLinkRepository links)
        BuildExecute(ApplicationDbContext db, long chatId = 5, int messageId = 10)
    {
        var skedda = new StubSkedda();
        var scheduler = new RecordingScheduler();
        var notification = new Mock<INotificationSender>();
        notification.Setup(x => x.NotifyBookingSucceededAsync(It.IsAny<BookingUserConfig>(), It.IsAny<BookingSlot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TelegramNotificationResult(chatId, messageId));
        var links = new BookingCancellationLinkRepository(db, NullLogger<BookingCancellationLinkRepository>.Instance);
        var execute = new ExecuteBookingUseCase(
            skedda, notification.Object, new InMemoryBookingDeduplicationStore(), links, scheduler,
            NullLogger<ExecuteBookingUseCase>.Instance);
        return (execute, skedda, scheduler, links);
    }

    // ---- BookingFallbackUseCase over a real repository ----

    [Fact]
    public async Task Fallback_LoadsConfigFromDb_BooksOnce_SavesLinkAndSchedulesReminders()
    {
        await using var db = NewDb();
        var entity = SeedConfig(db, "fallback-user");
        var (execute, skedda, scheduler, links) = BuildExecute(db);
        var fallback = new BookingFallbackUseCase(new UserBookingConfigRepository(db), skedda, execute);
        var start = new DateTimeOffset(2030, 4, 6, entity.Hour, 0, 0, TimeSpan.Zero);

        await fallback.ExecuteAsync(entity.Id, start, CancellationToken.None);

        Assert.Equal(1, skedda.BookCalls);
        var link = await links.GetByMessageAsync(5, 10, CancellationToken.None);
        Assert.NotNull(link);
        Assert.Equal("sk-1", link.SkeddaBookingId);
        Assert.Equal("job-24h", link.AttendanceReminder24hJobId);
        Assert.Equal("job-2h", link.AttendanceReminder2hJobId);
        Assert.Equal(2, scheduler.AttendanceChecks.Count);
    }

    [Fact]
    public async Task Fallback_Dedupes_WhenPreciseBookingAlreadyPostedSameSlot()
    {
        await using var db = NewDb();
        var entity = SeedConfig(db, "dedupe-user");
        var (execute, skedda, _, _) = BuildExecute(db);
        var repo = new UserBookingConfigRepository(db);
        var fallback = new BookingFallbackUseCase(repo, skedda, execute);
        var domain = await repo.GetByIdAsync(entity.Id, CancellationToken.None);
        Assert.NotNull(domain);
        var slot = new BookingSlot(new DateTimeOffset(2030, 4, 6, entity.Hour, 0, 0, TimeSpan.Zero));
        var prepared = await skedda.PrepareBookingAsync(domain, slot, CancellationToken.None);

        await execute.ExecuteAsync(prepared, CancellationToken.None);
        await fallback.ExecuteAsync(entity.Id, slot.StartTime, CancellationToken.None);

        Assert.Equal(1, skedda.BookCalls);
    }

    [Fact]
    public async Task Fallback_UnknownConfig_Throws_WithoutBooking()
    {
        await using var db = NewDb();
        var (execute, skedda, _, _) = BuildExecute(db);
        var fallback = new BookingFallbackUseCase(new UserBookingConfigRepository(db), skedda, execute);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fallback.ExecuteAsync(987654, new DateTimeOffset(2030, 1, 1, 10, 0, 0, TimeSpan.Zero), CancellationToken.None));
        Assert.Contains("987654", ex.Message);
        Assert.Equal(0, skedda.BookCalls);
    }

    // ---- CancelBookingUseCase full cycle over a real repository ----

    [Fact]
    public async Task Cancel_FullCycle_CancelledThenAlreadyCancelled_DeletesReminderJobs()
    {
        await using var db = NewDb();
        var entity = SeedConfig(db, "cancel-user");
        var (execute, skedda, scheduler, links) = BuildExecute(db, chatId: 42, messageId: 77);
        var start = new DateTimeOffset(2030, 4, 6, entity.Hour, 0, 0, TimeSpan.Zero);
        var domain = await new UserBookingConfigRepository(db).GetByIdAsync(entity.Id, CancellationToken.None);
        Assert.NotNull(domain);
        await execute.ExecuteAsync(await skedda.PrepareBookingAsync(domain, new BookingSlot(start), CancellationToken.None), CancellationToken.None);
        var cancel = new CancelBookingUseCase(links, skedda, scheduler, NullLogger<CancelBookingUseCase>.Instance);

        Assert.Equal(CancelBookingStatus.Cancelled, await cancel.ExecuteAsync(42, 77, 78, CancellationToken.None));
        Assert.Equal(1, skedda.CancelCalls);
        Assert.Contains("job-24h", scheduler.Deleted);
        Assert.Contains("job-2h", scheduler.Deleted);

        Assert.Equal(CancelBookingStatus.AlreadyCancelled, await cancel.ExecuteAsync(42, 77, 79, CancellationToken.None));
        Assert.Equal(1, skedda.CancelCalls); // no second Skedda cancel
    }

    [Fact]
    public async Task Cancel_UnknownLink_ReturnsNotFound_WithoutSkeddaCall()
    {
        await using var db = NewDb();
        var (execute, skedda, scheduler, links) = BuildExecute(db);
        _ = execute;
        var cancel = new CancelBookingUseCase(links, skedda, scheduler, NullLogger<CancelBookingUseCase>.Instance);

        Assert.Equal(CancelBookingStatus.NotFound, await cancel.ExecuteAsync(1, 2, 3, CancellationToken.None));
        Assert.Equal(0, skedda.CancelCalls);
    }

    // ---- UpdateBookingScheduleUseCase + ScheduleBookingsUseCase over a real repository ----

    [Fact]
    public async Task UpdateSchedule_PersistsToDb_AndReschedules()
    {
        await using var db = NewDb();
        var entity = SeedConfig(db, "sched-user", DayOfWeek.Monday, 10);
        var scheduler = new RecordingScheduler();
        var useCase = new UpdateBookingScheduleUseCase(new UserBookingConfigRepository(db), scheduler);

        var result = await useCase.ExecuteAsync(entity.Id, (int)DayOfWeek.Saturday, 19, CancellationToken.None);

        Assert.Equal(UpdateBookingScheduleStatus.Updated, result.Status);
        Assert.NotNull(result.UserConfig);
        Assert.Equal(DayOfWeek.Saturday, result.UserConfig.DayOfWeek);
        Assert.Equal(19, result.UserConfig.Hour);
        var reread = await db.UserConfigs.AsNoTracking().SingleAsync(x => x.Id == entity.Id);
        Assert.Equal(DayOfWeek.Saturday, reread.DayOfWeek);
        var scheduled = Assert.Single(scheduler.Recurring);
        Assert.Equal(entity.Id, scheduled.Id);
        Assert.Equal(19, scheduled.Hour);
    }

    [Fact]
    public async Task ScheduleBookings_FansOutToEveryDbConfig()
    {
        await using var db = NewDb();
        SeedConfig(db, "fanout-a");
        SeedConfig(db, "fanout-b");
        var scheduler = new RecordingScheduler();
        var useCase = new ScheduleBookingsUseCase(new UserBookingConfigRepository(db), scheduler);

        await useCase.ExecuteAsync(CancellationToken.None);

        Assert.Equal(2, scheduler.Recurring.Count);
        Assert.Contains(scheduler.Recurring, x => x.Username == "fanout-a");
        Assert.Contains(scheduler.Recurring, x => x.Username == "fanout-b");
    }

    [Fact]
    public async Task ScheduleBookings_EmptyDb_SchedulesNothing()
    {
        await using var db = NewDb();
        var scheduler = new RecordingScheduler();
        var useCase = new ScheduleBookingsUseCase(new UserBookingConfigRepository(db), scheduler);

        await useCase.ExecuteAsync(CancellationToken.None);

        Assert.Empty(scheduler.Recurring);
    }

    [Fact]
    public async Task PrepareForConfig_UnknownId_Throws_WithoutSchedulerCall()
    {
        await using var db = NewDb();
        var scheduler = new RecordingScheduler();
        var skedda = new StubSkedda();
        var prepare = new PrepareBookingUseCase(skedda, scheduler, new FixedClock(new DateTimeOffset(2026, 5, 19, 12, 0, 0, TimeSpan.Zero)));
        var useCase = new PrepareBookingForConfigUseCase(new UserBookingConfigRepository(db), prepare);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => useCase.ExecuteAsync(123456, true, CancellationToken.None));
        Assert.Equal(0, scheduler.PreciseCount);
    }

    // ---- HangfireBookingScheduler forwarding (mocked IBackgroundJobClient) ----

    [Fact]
    public void HangfireScheduler_ScheduleFallback_ForwardsExactRunAt()
    {
        // Schedule<T>/Delete are Hangfire extension methods; the mockable seam is Create/ChangeState.
        var jobs = new Mock<IBackgroundJobClient>();
        Hangfire.Common.Job? createdJob = null;
        Hangfire.States.IState? createdState = null;
        jobs.Setup(x => x.Create(It.IsAny<Hangfire.Common.Job>(), It.IsAny<Hangfire.States.IState>()))
            .Callback<Hangfire.Common.Job, Hangfire.States.IState>((j, s) => { createdJob = j; createdState = s; })
            .Returns("hangfire-id");
        var scheduler = new HangfireBookingScheduler(jobs.Object, Mock.Of<IPreciseBookingScheduler>());
        var runAt = new DateTimeOffset(2030, 5, 1, 12, 0, 3, TimeSpan.Zero);
        var start = new DateTimeOffset(2030, 5, 4, 10, 0, 0, TimeSpan.Zero);

        scheduler.ScheduleFallback(7, start, runAt);

        jobs.Verify(x => x.Create(It.IsAny<Hangfire.Common.Job>(), It.IsAny<Hangfire.States.IState>()), Times.Once);
        Assert.NotNull(createdJob);
        Assert.Equal(typeof(BookingFallbackUseCase), createdJob.Type);
        Assert.Equal(nameof(BookingFallbackUseCase.ExecuteAsync), createdJob.Method.Name);
        Assert.Equal([7, start, CancellationToken.None], createdJob.Args);
        var scheduled = Assert.IsType<Hangfire.States.ScheduledState>(createdState);
        Assert.Equal(runAt.UtcDateTime, scheduled.EnqueueAt);
    }

    [Fact]
    public void HangfireScheduler_DeleteAttendanceCheck_ForwardsJobId()
    {
        var jobs = new Mock<IBackgroundJobClient>();
        var scheduler = new HangfireBookingScheduler(jobs.Object, Mock.Of<IPreciseBookingScheduler>());

        scheduler.DeleteAttendanceCheck("job-123");

        jobs.Verify(x => x.ChangeState("job-123", It.IsAny<Hangfire.States.DeletedState>(), null), Times.Once);
    }

    [Fact]
    public void HangfireScheduler_ScheduleAttendanceCheck_FutureRunAt_PassesThrough()
    {
        var jobs = new Mock<IBackgroundJobClient>();
        Hangfire.Common.Job? futureJob = null;
        Hangfire.States.IState? futureState = null;
        jobs.Setup(x => x.Create(It.IsAny<Hangfire.Common.Job>(), It.IsAny<Hangfire.States.IState>()))
            .Callback<Hangfire.Common.Job, Hangfire.States.IState>((j, s) => { futureJob = j; futureState = s; })
            .Returns("job-1");
        var scheduler = new HangfireBookingScheduler(jobs.Object, Mock.Of<IPreciseBookingScheduler>());
        var runAt = DateTimeOffset.UtcNow.AddHours(5);

        var id = scheduler.ScheduleAttendanceCheck(1, 2, runAt.AddHours(19), AttendanceReminderUseCase.ReminderType24h, runAt);

        Assert.Equal("job-1", id);
        Assert.NotNull(futureJob);
        Assert.Equal(typeof(AttendanceReminderUseCase), futureJob.Type);
        var scheduled = Assert.IsType<Hangfire.States.ScheduledState>(futureState);
        Assert.Equal(runAt.UtcDateTime, scheduled.EnqueueAt);
    }

    [Fact]
    public void HangfireScheduler_ScheduleAttendanceCheck_PastRunAt_ClampedToNearNow()
    {
        var jobs = new Mock<IBackgroundJobClient>();
        Hangfire.States.IState? pastState = null;
        jobs.Setup(x => x.Create(It.IsAny<Hangfire.Common.Job>(), It.IsAny<Hangfire.States.IState>()))
            .Callback<Hangfire.Common.Job, Hangfire.States.IState>((_, s) => pastState = s)
            .Returns("job-1");
        var scheduler = new HangfireBookingScheduler(jobs.Object, Mock.Of<IPreciseBookingScheduler>());
        var before = DateTimeOffset.UtcNow;

        scheduler.ScheduleAttendanceCheck(1, 2, before.AddHours(1), AttendanceReminderUseCase.ReminderType2h, before.AddHours(-2));

        var clamped = Assert.IsType<Hangfire.States.ScheduledState>(pastState);
        Assert.True(clamped.EnqueueAt >= before.UtcDateTime, $"clamped runAt {clamped.EnqueueAt:o} should not be in the past");
        Assert.True(clamped.EnqueueAt <= DateTimeOffset.UtcNow.AddMinutes(1).UtcDateTime, $"clamped runAt {clamped.EnqueueAt:o} should be ~now");
    }

    // ---- WeatherApiWeatherForecastProvider over a stubbed handler ----

    private static WeatherApiWeatherForecastProvider WeatherProvider(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        WeatherOptions? options = null)
    {
        var handler = new FuncHandler(responder);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:9") };
        return new WeatherApiWeatherForecastProvider(
            http,
            Microsoft.Extensions.Options.Options.Create(options ?? new WeatherOptions { Enabled = true, ApiKey = "key" }),
            NullLogger<WeatherApiWeatherForecastProvider>.Instance);
    }

    private sealed class FuncHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public FuncHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_responder(request));
    }

    private static string WeatherJson(long epoch, double tempC, int chance, double precipMm)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return "{\"forecast\":{\"forecastday\":[{\"hour\":[{\"time_epoch\":" + epoch
            + ",\"temp_c\":" + tempC.ToString(inv)
            + ",\"chance_of_rain\":" + chance
            + ",\"precip_mm\":" + precipMm.ToString(inv) + "}]}]}}";
    }

    [Fact]
    public async Task Weather_Disabled_ReturnsNull_WithoutHttpCall()
    {
        var calls = 0;
        var provider = WeatherProvider(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }, new WeatherOptions { Enabled = false, ApiKey = "key" });

        Assert.Null(await provider.GetForecastAsync(new DateTimeOffset(2030, 6, 1, 10, 30, 0, TimeSpan.Zero), CancellationToken.None));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Weather_MissingApiKey_ReturnsNull_WithoutHttpCall()
    {
        var calls = 0;
        var provider = WeatherProvider(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }, new WeatherOptions { Enabled = true, ApiKey = "" });

        Assert.Null(await provider.GetForecastAsync(new DateTimeOffset(2030, 6, 1, 10, 30, 0, TimeSpan.Zero), CancellationToken.None));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Weather_Success_ParsesMatchingHour_AndFlagsRain()
    {
        var at = new DateTimeOffset(2030, 6, 1, 10, 30, 0, TimeSpan.Zero);
        var hourEpoch = new DateTimeOffset(2030, 6, 1, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        string? url = null;
        var provider = WeatherProvider(req =>
        {
            url = req.RequestUri!.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(WeatherJson(hourEpoch, 21.5, 80, 0.0), System.Text.Encoding.UTF8, "application/json")
            };
        });

        var forecast = await provider.GetForecastAsync(at, CancellationToken.None);

        Assert.NotNull(forecast);
        Assert.Equal(21.5, forecast.TemperatureC);
        Assert.Equal(80, forecast.PrecipitationProbabilityPercent);
        Assert.True(forecast.IsRainExpected);
        Assert.Contains("forecast.json", url);
        Assert.Contains("days=3", url);
    }

    [Fact]
    public async Task Weather_NoMatchingHour_ReturnsNull()
    {
        var at = new DateTimeOffset(2030, 6, 1, 10, 30, 0, TimeSpan.Zero);
        var otherEpoch = new DateTimeOffset(2030, 6, 2, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var provider = WeatherProvider(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(WeatherJson(otherEpoch, 21.5, 0, 0.0), System.Text.Encoding.UTF8, "application/json")
        });

        Assert.Null(await provider.GetForecastAsync(at, CancellationToken.None));
    }

    [Fact]
    public async Task Weather_ServerError_ThrowsHttpRequestException()
    {
        var provider = WeatherProvider(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("bad", System.Text.Encoding.UTF8, "text/plain")
        });

        await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.GetForecastAsync(new DateTimeOffset(2030, 6, 1, 10, 0, 0, TimeSpan.Zero), CancellationToken.None));
    }

    // ---- TelegramNotificationSender over a stubbed handler + real chat repo ----

    private static TelegramNotificationSender TelegramSender(
        ApplicationDbContext db,
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        List<HttpRequestMessage>? seen = null)
    {
        var http = new HttpClient(new FuncHandler(req =>
        {
            seen?.Add(req);
            return responder(req);
        }));
        return new TelegramNotificationSender(
            http,
            Microsoft.Extensions.Options.Options.Create(new TelegramOptions { BotToken = "bot-token" }),
            new TelegramChatRepository(db),
            NullLogger<TelegramNotificationSender>.Instance);
    }

    [Fact]
    public async Task Telegram_NoActiveChat_ReturnsZero_WithoutHttpCall()
    {
        await using var db = NewDb();
        var calls = 0;
        var sender = TelegramSender(db, _ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var slot = new BookingSlot(new DateTimeOffset(2030, 6, 1, 10, 0, 0, TimeSpan.Zero));

        var result = await sender.NotifyBookingSucceededAsync(
            new BookingUserConfig(1, "u", "p", "r", "v", "vu", DayOfWeek.Monday, 10), slot, CancellationToken.None);

        Assert.Equal(new TelegramNotificationResult(0, 0), result);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Telegram_Success_ReturnsChatAndMessageId()
    {
        await using var db = NewDb();
        db.TelegramChats.Add(new TelegramChatEntity { Name = "main", ChatId = 555, IsActive = true });
        await db.SaveChangesAsync();
        var seen = new List<HttpRequestMessage>();
        var sender = TelegramSender(db, _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"ok":true,"result":{"message_id":42}}""", System.Text.Encoding.UTF8, "application/json")
        }, seen);
        var slot = new BookingSlot(new DateTimeOffset(2030, 6, 1, 10, 0, 0, TimeSpan.Zero));

        var result = await sender.NotifyBookingSucceededAsync(
            new BookingUserConfig(1, "u", "p", "r", "v", "vu", DayOfWeek.Monday, 10), slot, CancellationToken.None);

        Assert.Equal(new TelegramNotificationResult(555, 42), result);
        var req = Assert.Single(seen);
        Assert.Contains("botbot-token", req.RequestUri!.ToString());
        var body = await req.Content!.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(555, doc.RootElement.GetProperty("chat_id").GetInt64());
        Assert.Contains("Забронював", doc.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Telegram_HttpFailure_ReturnsZeroInsteadOfThrowing()
    {
        await using var db = NewDb();
        db.TelegramChats.Add(new TelegramChatEntity { Name = "main", ChatId = 555, IsActive = true });
        await db.SaveChangesAsync();
        var sender = TelegramSender(db, _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("bad request", System.Text.Encoding.UTF8, "text/plain")
        });
        var slot = new BookingSlot(new DateTimeOffset(2030, 6, 1, 10, 0, 0, TimeSpan.Zero));

        var result = await sender.NotifyBookingSucceededAsync(
            new BookingUserConfig(1, "u", "p", "r", "v", "vu", DayOfWeek.Monday, 10), slot, CancellationToken.None);

        Assert.Equal(new TelegramNotificationResult(0, 0), result);
    }

    // ---- PreparationHealthCheck over a real repository ----

    [Fact]
    public async Task HealthCheck_Succeeds_WhenPreparationSucceeds()
    {
        await using var db = NewDb();
        SeedConfig(db, "health-user");
        var prepare = new PrepareBookingUseCase(
            new StubSkedda(), Mock.Of<IBookingScheduler>(),
            new FixedClock(new DateTimeOffset(2026, 5, 19, 12, 0, 0, TimeSpan.Zero)));
        var check = new PreparationHealthCheck(
            prepare, new UserBookingConfigRepository(db), NullLogger<PreparationHealthCheck>.Instance);

        var result = await check.CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext(), CancellationToken.None);

        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task HealthCheck_Unhealthy_WhenPreparationThrows()
    {
        await using var db = NewDb();
        SeedConfig(db, "health-user");
        var skedda = new StubSkedda
        {
            PrepareFunc = (_, _) => throw new HttpRequestException("skedda down")
        };
        var prepare = new PrepareBookingUseCase(
            skedda, Mock.Of<IBookingScheduler>(),
            new FixedClock(new DateTimeOffset(2026, 5, 19, 12, 0, 0, TimeSpan.Zero)));
        var check = new PreparationHealthCheck(
            prepare, new UserBookingConfigRepository(db), NullLogger<PreparationHealthCheck>.Instance);

        var result = await check.CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext(), CancellationToken.None);

        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy, result.Status);
    }
}
