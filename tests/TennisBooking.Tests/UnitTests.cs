using System.Net;
using System.Text;
using System.Text.Json;
using Hangfire;
using Hangfire.Dashboard;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using TennisBooking.Application.Abstractions;
using TennisBooking.Application.Booking;
using TennisBooking.Auth;
using TennisBooking.DAL;
using TennisBooking.DAL.Models;
using TennisBooking.Domain.Booking;
using TennisBooking.HealthChecks;
using TennisBooking.Infrastructure.Persistence;
using TennisBooking.Infrastructure.Scheduling;
using TennisBooking.Infrastructure.Skedda;
using TennisBooking.Infrastructure.Telegram;
using TennisBooking.Options;
using Xunit;

namespace TennisBooking.Tests;

public class UnitTests
{
    [Fact]
    public async Task PrepareBooking_WithNullUserConfig_ReturnsNull()
    {
        var useCase = new PrepareBookingUseCase(
            Mock.Of<ISkeddaClient>(),
            new RecordingScheduler(),
            new FixedClock(new DateTimeOffset(2026, 5, 19, 12, 0, 0, TimeSpan.Zero)));

        var result = await useCase.ExecuteAsync(null, false, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task PrepareBooking_SchedulesPreciseAndFallback_WithMinimalFallbackArguments()
    {
        var user = BasicDomainConfig();
        var slot = new BookingSlot(new DateTimeOffset(2030, 6, 3, 10, 0, 0, TimeSpan.Zero));
        var prepared = Prepared(user, slot);
        var skedda = new Mock<ISkeddaClient>();
        skedda.Setup(x => x.PrepareBookingAsync(user, It.IsAny<BookingSlot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(prepared);
        var scheduler = new RecordingScheduler();

        var useCase = new PrepareBookingUseCase(
            skedda.Object,
            scheduler,
            new FixedClock(new DateTimeOffset(2026, 5, 19, 12, 0, 0, TimeSpan.Zero)));

        await useCase.ExecuteAsync(user, true, CancellationToken.None);

        Assert.Same(prepared, scheduler.PreciseBooking);
        Assert.Equal(user.Id, scheduler.FallbackUserConfigId);
        Assert.Equal(slot.StartTime, scheduler.FallbackStartTime);
    }

    [Fact]
    public async Task ExecuteBooking_DoesNotPostTwice_ForSameSlot()
    {
        var skedda = new Mock<ISkeddaClient>();
        skedda.Setup(x => x.BookAsync(It.IsAny<PreparedBooking>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SkeddaBookingResult("1"));
        var notification = new Mock<INotificationSender>();
        notification.Setup(x => x.NotifyBookingSucceededAsync(It.IsAny<BookingUserConfig>(), It.IsAny<BookingSlot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TelegramNotificationResult(5, 10));
        var useCase = new ExecuteBookingUseCase(
            skedda.Object,
            notification.Object,
            new InMemoryBookingDeduplicationStore(),
            Mock.Of<IBookingCancellationLinkRepository>(),
            Mock.Of<IBookingScheduler>(),
            NullLogger<ExecuteBookingUseCase>.Instance);
        var booking = Prepared(BasicDomainConfig(), new BookingSlot(new DateTimeOffset(2030, 6, 15, 10, 0, 0, TimeSpan.Zero)));

        await useCase.ExecuteAsync(booking, CancellationToken.None);
        await useCase.ExecuteAsync(booking, CancellationToken.None);

        skedda.Verify(x => x.BookAsync(booking, It.IsAny<CancellationToken>()), Times.Once);
        notification.Verify(x => x.NotifyBookingSucceededAsync(booking.UserConfig, booking.Slot, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteBooking_SkipsTelegramFollowUps_WhenNotificationWasNotSent()
    {
        var skedda = new Mock<ISkeddaClient>();
        skedda.Setup(x => x.BookAsync(It.IsAny<PreparedBooking>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SkeddaBookingResult("1"));
        var notification = new Mock<INotificationSender>();
        notification.Setup(x => x.NotifyBookingSucceededAsync(It.IsAny<BookingUserConfig>(), It.IsAny<BookingSlot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TelegramNotificationResult(0, 0));
        var links = new Mock<IBookingCancellationLinkRepository>();
        var scheduler = new Mock<IBookingScheduler>();
        var useCase = new ExecuteBookingUseCase(
            skedda.Object,
            notification.Object,
            new InMemoryBookingDeduplicationStore(),
            links.Object,
            scheduler.Object,
            NullLogger<ExecuteBookingUseCase>.Instance);
        var booking = Prepared(BasicDomainConfig(), new BookingSlot(new DateTimeOffset(2030, 6, 15, 10, 0, 0, TimeSpan.Zero)));

        await useCase.ExecuteAsync(booking, TestContext.Current.CancellationToken);

        links.Verify(
            x => x.SaveAsync(
                It.IsAny<BookingUserConfig>(),
                It.IsAny<BookingSlot>(),
                It.IsAny<long>(),
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        scheduler.Verify(
            x => x.ScheduleAttendanceCheck(
                It.IsAny<long>(),
                It.IsAny<int>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<string>(),
                It.IsAny<DateTimeOffset>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteBooking_PersistsScheduledReminderJobIds()
    {
        var skedda = new Mock<ISkeddaClient>();
        skedda.Setup(x => x.BookAsync(It.IsAny<PreparedBooking>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SkeddaBookingResult("1"));
        var notification = new Mock<INotificationSender>();
        notification.Setup(x => x.NotifyBookingSucceededAsync(It.IsAny<BookingUserConfig>(), It.IsAny<BookingSlot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TelegramNotificationResult(5, 10));
        var links = new Mock<IBookingCancellationLinkRepository>();
        links.Setup(x => x.SaveReminderJobIdAsync(5, 10, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var scheduler = new Mock<IBookingScheduler>();
        scheduler.Setup(x => x.ScheduleAttendanceCheck(5, 10, It.IsAny<DateTimeOffset>(), AttendanceReminderUseCase.ReminderType24h, It.IsAny<DateTimeOffset>()))
            .Returns("job-24h");
        scheduler.Setup(x => x.ScheduleAttendanceCheck(5, 10, It.IsAny<DateTimeOffset>(), AttendanceReminderUseCase.ReminderType2h, It.IsAny<DateTimeOffset>()))
            .Returns("job-2h");
        var useCase = new ExecuteBookingUseCase(
            skedda.Object,
            notification.Object,
            new InMemoryBookingDeduplicationStore(),
            links.Object,
            scheduler.Object,
            NullLogger<ExecuteBookingUseCase>.Instance);

        await useCase.ExecuteAsync(
            Prepared(BasicDomainConfig(), new BookingSlot(new DateTimeOffset(2030, 6, 15, 10, 0, 0, TimeSpan.Zero))),
            CancellationToken.None);

        links.Verify(x => x.SaveReminderJobIdAsync(5, 10, AttendanceReminderUseCase.ReminderType24h, "job-24h", It.IsAny<CancellationToken>()), Times.Once);
        links.Verify(x => x.SaveReminderJobIdAsync(5, 10, AttendanceReminderUseCase.ReminderType2h, "job-2h", It.IsAny<CancellationToken>()), Times.Once);
        scheduler.Verify(x => x.DeleteAttendanceCheck(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteBooking_DeletesReminderScheduledAfterCancellation()
    {
        var skedda = new Mock<ISkeddaClient>();
        skedda.Setup(x => x.BookAsync(It.IsAny<PreparedBooking>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SkeddaBookingResult("1"));
        var notification = new Mock<INotificationSender>();
        notification.Setup(x => x.NotifyBookingSucceededAsync(It.IsAny<BookingUserConfig>(), It.IsAny<BookingSlot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TelegramNotificationResult(5, 10));
        var links = new Mock<IBookingCancellationLinkRepository>();
        links.Setup(x => x.SaveReminderJobIdAsync(5, 10, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var scheduler = new Mock<IBookingScheduler>();
        scheduler.SetupSequence(x => x.ScheduleAttendanceCheck(5, 10, It.IsAny<DateTimeOffset>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>()))
            .Returns("job-24h")
            .Returns("job-2h");
        var useCase = new ExecuteBookingUseCase(
            skedda.Object,
            notification.Object,
            new InMemoryBookingDeduplicationStore(),
            links.Object,
            scheduler.Object,
            NullLogger<ExecuteBookingUseCase>.Instance);

        await useCase.ExecuteAsync(
            Prepared(BasicDomainConfig(), new BookingSlot(new DateTimeOffset(2030, 6, 15, 10, 0, 0, TimeSpan.Zero))),
            CancellationToken.None);

        scheduler.Verify(x => x.DeleteAttendanceCheck("job-24h"), Times.Once);
        scheduler.Verify(x => x.DeleteAttendanceCheck("job-2h"), Times.Once);
    }

    [Fact]
    public async Task BookingFallback_LoadsConfigAndBooks()
    {
        await using var db = NewInMemoryDb();
        var entity = BasicEntityConfig();
        db.UserConfigs.Add(entity);
        await db.SaveChangesAsync();

        var skedda = new Mock<ISkeddaClient>();
        skedda.Setup(x => x.BookAsync(It.IsAny<PreparedBooking>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SkeddaBookingResult("1"));
        var notification = new Mock<INotificationSender>();
        notification.Setup(x => x.NotifyBookingSucceededAsync(It.IsAny<BookingUserConfig>(), It.IsAny<BookingSlot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TelegramNotificationResult(5, 10));
        var prepared = Prepared(ToDomain(entity), new BookingSlot(new DateTimeOffset(2030, 1, 1, entity.Hour, 0, 0, TimeSpan.Zero)));
        skedda.Setup(x => x.PrepareBookingAsync(It.IsAny<BookingUserConfig>(), It.IsAny<BookingSlot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(prepared);
        var execute = new ExecuteBookingUseCase(
            skedda.Object,
            notification.Object,
            new InMemoryBookingDeduplicationStore(),
            Mock.Of<IBookingCancellationLinkRepository>(),
            Mock.Of<IBookingScheduler>(),
            NullLogger<ExecuteBookingUseCase>.Instance);
        var fallback = new BookingFallbackUseCase(new UserBookingConfigRepository(db), skedda.Object, execute);

        await fallback.ExecuteAsync(entity.Id, prepared.Slot.StartTime, CancellationToken.None);

        skedda.Verify(x => x.BookAsync(prepared, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BookingFallback_Throws_WhenUserConfigMissing()
    {
        await using var db = NewInMemoryDb();
        var fallback = new BookingFallbackUseCase(
            new UserBookingConfigRepository(db),
            Mock.Of<ISkeddaClient>(),
            new ExecuteBookingUseCase(
                Mock.Of<ISkeddaClient>(),
                Mock.Of<INotificationSender>(),
                new InMemoryBookingDeduplicationStore(),
                Mock.Of<IBookingCancellationLinkRepository>(),
                Mock.Of<IBookingScheduler>(),
                NullLogger<ExecuteBookingUseCase>.Instance));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fallback.ExecuteAsync(999, DateTimeOffset.UtcNow, CancellationToken.None));
    }

    [Fact]
    public async Task UpdateBookingSchedule_UpdatesDbAndReschedulesRecurringPreparation()
    {
        await using var db = NewInMemoryDb();
        db.UserConfigs.Add(BasicEntityConfig());
        await db.SaveChangesAsync();
        var scheduler = new RecordingScheduler();
        var useCase = new UpdateBookingScheduleUseCase(new UserBookingConfigRepository(db), scheduler);

        var result = await useCase.ExecuteAsync(1, (int)DayOfWeek.Thursday, 18, CancellationToken.None);

        Assert.Equal(UpdateBookingScheduleStatus.Updated, result.Status);
        var entity = await db.UserConfigs.SingleAsync();
        Assert.Equal(DayOfWeek.Thursday, entity.DayOfWeek);
        Assert.Equal(18, entity.Hour);
        Assert.NotNull(scheduler.RecurringUserConfig);
        Assert.Equal(entity.Id, scheduler.RecurringUserConfig.Id);
        Assert.Equal(DayOfWeek.Thursday, scheduler.RecurringUserConfig.DayOfWeek);
        Assert.Equal(18, scheduler.RecurringUserConfig.Hour);
    }

    [Fact]
    public async Task UpdateBookingSchedule_RejectsInvalidHour_WithoutDbUpdateOrReschedule()
    {
        await using var db = NewInMemoryDb();
        db.UserConfigs.Add(BasicEntityConfig());
        await db.SaveChangesAsync();
        var scheduler = new RecordingScheduler();
        var useCase = new UpdateBookingScheduleUseCase(new UserBookingConfigRepository(db), scheduler);

        var result = await useCase.ExecuteAsync(1, (int)DayOfWeek.Thursday, 24, CancellationToken.None);

        Assert.Equal(UpdateBookingScheduleStatus.Invalid, result.Status);
        var entity = await db.UserConfigs.SingleAsync();
        Assert.Equal(DayOfWeek.Monday, entity.DayOfWeek);
        Assert.Equal(10, entity.Hour);
        Assert.Null(scheduler.RecurringUserConfig);
    }

    [Fact]
    public async Task AttendanceReminder_SendsMessage_WhenThumbsUpCountIsLow()
    {
        var link = new BookingCancellationLink(
            BasicDomainConfig(),
            new BookingSlot(new DateTimeOffset(2030, 6, 15, 10, 0, 0, TimeSpan.Zero)),
            5,
            10,
            "skedda-1",
            DateTimeOffset.UtcNow,
            null,
            null,
            null);
        var links = new Mock<IBookingCancellationLinkRepository>();
        links.Setup(x => x.GetByMessageAsync(5, 10, It.IsAny<CancellationToken>())).ReturnsAsync(link);
        links.Setup(x => x.TryMarkReminderSentAsync(5, 10, AttendanceReminderUseCase.ReminderType24h, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var notification = new Mock<INotificationSender>();
        notification.Setup(x => x.GetThumbsUpReactionCountAsync(5, 10, It.IsAny<CancellationToken>())).ReturnsAsync(1);
        var useCase = new AttendanceReminderUseCase(links.Object, notification.Object, NullLogger<AttendanceReminderUseCase>.Instance);

        await useCase.ExecuteAsync(5, 10, AttendanceReminderUseCase.ReminderType24h, CancellationToken.None);

        notification.Verify(
            x => x.NotifyMessageAsync(
                It.Is<string>(text => text.Contains("завтра корт заброньований", StringComparison.Ordinal)),
                It.IsAny<CancellationToken>(),
                10),
            Times.Once);
    }

    [Fact]
    public async Task AttendanceReminder_DoesNotSend_WhenThumbsUpCountIsEnough()
    {
        var link = new BookingCancellationLink(
            BasicDomainConfig(),
            new BookingSlot(new DateTimeOffset(2030, 6, 15, 10, 0, 0, TimeSpan.Zero)),
            5,
            10,
            "skedda-1",
            DateTimeOffset.UtcNow,
            null,
            null,
            null);
        var links = new Mock<IBookingCancellationLinkRepository>();
        links.Setup(x => x.GetByMessageAsync(5, 10, It.IsAny<CancellationToken>())).ReturnsAsync(link);
        links.Setup(x => x.TryMarkReminderSentAsync(5, 10, AttendanceReminderUseCase.ReminderType2h, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var notification = new Mock<INotificationSender>();
        notification.Setup(x => x.GetThumbsUpReactionCountAsync(5, 10, It.IsAny<CancellationToken>())).ReturnsAsync(2);
        var useCase = new AttendanceReminderUseCase(links.Object, notification.Object, NullLogger<AttendanceReminderUseCase>.Instance);

        await useCase.ExecuteAsync(5, 10, AttendanceReminderUseCase.ReminderType2h, CancellationToken.None);

        notification.Verify(x => x.NotifyMessageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<int?>()), Times.Never);
    }

    [Fact]
    public async Task CancelBooking_CancelsSkeddaAndDeletesStoredReminderJobs()
    {
        var active = BookingLink(null, "job-24h", "job-2h");
        var cancelled = BookingLink(DateTimeOffset.UtcNow, "job-24h", "job-2h");
        var links = new Mock<IBookingCancellationLinkRepository>();
        links.SetupSequence(x => x.GetByReplyAsync(5, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(active)
            .ReturnsAsync(cancelled);
        links.Setup(x => x.TryMarkCancelledAsync(5, 10, 11, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var skedda = new Mock<ISkeddaClient>();
        var scheduler = new Mock<IBookingScheduler>();
        var useCase = new CancelBookingUseCase(links.Object, skedda.Object, scheduler.Object, NullLogger<CancelBookingUseCase>.Instance);

        var status = await useCase.ExecuteAsync(5, 10, 11, CancellationToken.None);

        Assert.Equal(CancelBookingStatus.Cancelled, status);
        skedda.Verify(x => x.CancelAsync(It.IsAny<PreparedBooking>(), "skedda-1", It.IsAny<CancellationToken>()), Times.Once);
        scheduler.Verify(x => x.DeleteAttendanceCheck("job-24h"), Times.Once);
        scheduler.Verify(x => x.DeleteAttendanceCheck("job-2h"), Times.Once);
    }

    [Fact]
    public async Task CancelBooking_RetriesReminderDeletion_WhenAlreadyCancelled()
    {
        var links = new Mock<IBookingCancellationLinkRepository>();
        links.Setup(x => x.GetByReplyAsync(5, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(BookingLink(DateTimeOffset.UtcNow, "job-24h", "job-2h"));
        var skedda = new Mock<ISkeddaClient>();
        var scheduler = new Mock<IBookingScheduler>();
        var useCase = new CancelBookingUseCase(links.Object, skedda.Object, scheduler.Object, NullLogger<CancelBookingUseCase>.Instance);

        var status = await useCase.ExecuteAsync(5, 10, 11, CancellationToken.None);

        Assert.Equal(CancelBookingStatus.AlreadyCancelled, status);
        skedda.Verify(x => x.CancelAsync(It.IsAny<PreparedBooking>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        scheduler.Verify(x => x.DeleteAttendanceCheck("job-24h"), Times.Once);
        scheduler.Verify(x => x.DeleteAttendanceCheck("job-2h"), Times.Once);
    }

    [Fact]
    public async Task CancelBooking_HandlesLegacyCancelledLinkWithoutStoredReminderJobs()
    {
        var links = new Mock<IBookingCancellationLinkRepository>();
        links.Setup(x => x.GetByReplyAsync(5, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(BookingLink(DateTimeOffset.UtcNow, null, null));
        var scheduler = new Mock<IBookingScheduler>();
        var useCase = new CancelBookingUseCase(
            links.Object,
            Mock.Of<ISkeddaClient>(),
            scheduler.Object,
            NullLogger<CancelBookingUseCase>.Instance);

        var status = await useCase.ExecuteAsync(5, 10, 11, CancellationToken.None);

        Assert.Equal(CancelBookingStatus.AlreadyCancelled, status);
        scheduler.Verify(x => x.DeleteAttendanceCheck(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SkeddaClient_Throws_WhenTokenMissing()
    {
        using var server = new FakeSkeddaServer();
        server.Enqueue(HttpMethod.Get, "/account/login", ctx =>
        {
            ctx.Response.StatusCode = 200;
            return "<html>no token</html>";
        });

        var client = new SkeddaClient(
            new SingleClientHttpClientFactory(server.BaseUrl),
            Microsoft.Extensions.Options.Options.Create(new SkeddaOptions { ApiBaseUrl = server.BaseUrl }),
            NullLogger<SkeddaClient>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.PrepareBookingAsync(BasicDomainConfig(), new BookingSlot(DateTimeOffset.UtcNow), CancellationToken.None));
    }

    [Fact]
    public async Task SkeddaClient_BooksPreparedSlot()
    {
        using var skedda = new FakeSkeddaServer();
        var bookingPosts = 0;
        skedda.Enqueue(HttpMethod.Post, "/bookings", ctx =>
        {
            bookingPosts++;
            ctx.Response.StatusCode = 200;
            return """{"booking":{"id":"1"}}""";
        });
        var client = new SkeddaClient(
            new SingleClientHttpClientFactory(skedda.BaseUrl),
            Microsoft.Extensions.Options.Options.Create(new SkeddaOptions { ApiBaseUrl = skedda.BaseUrl }),
            NullLogger<SkeddaClient>.Instance);
        var booking = Prepared(BasicDomainConfig(), new BookingSlot(DateTimeOffset.UtcNow));

        await client.BookAsync(booking, CancellationToken.None);

        Assert.Equal(1, bookingPosts);
    }

    [Fact]
    public async Task TelegramNotificationSender_Notify_Works_And_HandlesMissingConfig()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = NewInMemoryDb();
        db.TelegramChats.Add(new TelegramChatEntity { Name = "Main", ChatId = 5, IsActive = true });
        await db.SaveChangesAsync(cancellationToken);

        var handler = new DelegateHandler((req, _) =>
        {
            var body = req.RequestUri!.AbsoluteUri.Contains("sendMessage", StringComparison.Ordinal)
                ? """{"ok":true,"result":{"message_id":123}}"""
                : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        });

        var sender = new TelegramNotificationSender(
            new HttpClient(handler),
            Microsoft.Extensions.Options.Options.Create(new TelegramOptions { BotToken = "x" }),
            new TelegramChatRepository(db),
            NullLogger<TelegramNotificationSender>.Instance);
        await sender.NotifyBookingSucceededAsync(BasicDomainConfig(), new BookingSlot(DateTimeOffset.UtcNow), cancellationToken);
    }

    [Fact]
    public async Task TelegramNotificationSender_UsesCurrentActiveChat()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = NewInMemoryDb();
        db.TelegramChats.AddRange(
            new TelegramChatEntity { Name = "Old", ChatId = 5, IsActive = true },
            new TelegramChatEntity { Name = "New", ChatId = 9, IsActive = false });
        await db.SaveChangesAsync(cancellationToken);

        var chatIds = new List<long>();
        var handler = new DelegateHandler(async (req, ct) =>
        {
            var json = await req.Content!.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            chatIds.Add(doc.RootElement.GetProperty("chat_id").GetInt64());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true,"result":{"message_id":123}}""", Encoding.UTF8, "application/json")
            };
        });

        var repository = new TelegramChatRepository(db);
        var sender = new TelegramNotificationSender(
            new HttpClient(handler),
            Microsoft.Extensions.Options.Options.Create(new TelegramOptions { BotToken = "x" }),
            repository,
            NullLogger<TelegramNotificationSender>.Instance);

        await sender.NotifyBookingSucceededAsync(BasicDomainConfig(), new BookingSlot(DateTimeOffset.UtcNow), cancellationToken);
        await repository.SetActiveAsync(2, cancellationToken);
        await sender.NotifyBookingSucceededAsync(BasicDomainConfig(), new BookingSlot(DateTimeOffset.UtcNow), cancellationToken);

        Assert.Equal(new long[] { 5, 9 }, chatIds);
    }

    [Fact]
    public async Task TelegramChatRepository_SetActive_SwitchesActiveChat()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = NewInMemoryDb();
        db.TelegramChats.AddRange(
            new TelegramChatEntity { Name = "Old", ChatId = 5, IsActive = true },
            new TelegramChatEntity { Name = "New", ChatId = 9, IsActive = false });
        await db.SaveChangesAsync(cancellationToken);

        var repository = new TelegramChatRepository(db);

        var selected = await repository.SetActiveAsync(2, cancellationToken);
        var chats = await repository.GetAllAsync(cancellationToken);

        Assert.NotNull(selected);
        Assert.Equal(9, selected.ChatId);
        Assert.Equal(9, Assert.Single(chats, x => x.IsActive).ChatId);
    }

    [Fact]
    public async Task PreparationHealthCheck_ReturnsHealthy_AndUnhealthy()
    {
        await using var healthyDb = NewInMemoryDb();
        var healthy = new PreparationHealthCheck(
            new PrepareBookingUseCase(Mock.Of<ISkeddaClient>(), new RecordingScheduler(), new FixedClock(DateTimeOffset.UtcNow)),
            new UserBookingConfigRepository(healthyDb),
            NullLogger<PreparationHealthCheck>.Instance);

        Assert.Equal(HealthStatus.Healthy, (await healthy.CheckHealthAsync(new HealthCheckContext())).Status);

        await using var unhealthyDb = NewInMemoryDb();
        unhealthyDb.UserConfigs.Add(BasicEntityConfig());
        await unhealthyDb.SaveChangesAsync();
        var badSkedda = new Mock<ISkeddaClient>();
        badSkedda.Setup(x => x.PrepareBookingAsync(It.IsAny<BookingUserConfig>(), It.IsAny<BookingSlot>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("bad"));
        var unhealthy = new PreparationHealthCheck(
            new PrepareBookingUseCase(badSkedda.Object, new RecordingScheduler(), new FixedClock(DateTimeOffset.UtcNow)),
            new UserBookingConfigRepository(unhealthyDb),
            NullLogger<PreparationHealthCheck>.Instance);

        Assert.Equal(HealthStatus.Unhealthy, (await unhealthy.CheckHealthAsync(new HealthCheckContext())).Status);
    }

    [Fact]
    public async Task NpgsqlHealthCheck_ReturnsUnhealthy_WhenConnectionFails()
    {
        var hc = new NpgsqlHealthCheck("Host=127.0.0.1;Port=1;Username=u;Password=p;Database=d;Timeout=1;Command Timeout=1");
        var result = await hc.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.NotNull(result.Exception);
    }

    [Fact]
    public void HangfireBasicAuthFilter_AuthorizationCases()
    {
        var filter = new HangfireBasicAuthFilter("user", "pass");
        var storage = new Mock<Hangfire.JobStorage>().Object;
        var options = new DashboardOptions();

        var ctxNoHeader = NewHttp();
        Assert.False(filter.Authorize(new AspNetCoreDashboardContext(storage, options, ctxNoHeader)));

        var httpBadScheme = NewHttp();
        httpBadScheme.Request.Headers.Authorization = "Bearer abc";
        Assert.False(filter.Authorize(new AspNetCoreDashboardContext(storage, options, httpBadScheme)));

        var httpBadCreds = NewHttp();
        httpBadCreds.Request.Headers.Authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("user:nope"));
        Assert.False(filter.Authorize(new AspNetCoreDashboardContext(storage, options, httpBadCreds)));

        var httpBadBase64 = NewHttp();
        httpBadBase64.Request.Headers.Authorization = "Basic not-base64";
        Assert.False(filter.Authorize(new AspNetCoreDashboardContext(storage, options, httpBadBase64)));

        var httpGood = NewHttp();
        httpGood.Request.Headers.Authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("user:pass"));
        Assert.True(filter.Authorize(new AspNetCoreDashboardContext(storage, options, httpGood)));
    }

    private static ApplicationDbContext NewInMemoryDb()
    {
        var opts = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(opts);
    }

    private static BookingUserConfig BasicDomainConfig() => new(
        1,
        "u",
        "p",
        "r",
        "v",
        "vu",
        DayOfWeek.Monday,
        10);

    private static UserConfig BasicEntityConfig() => new()
    {
        Id = 1,
        Username = "u",
        Password = "p",
        ResourceId = "r",
        Venue = "v",
        VenueUser = "vu",
        DayOfWeek = DayOfWeek.Monday,
        Hour = 10
    };

    private static BookingUserConfig ToDomain(UserConfig entity) => new(
        entity.Id,
        entity.Username,
        entity.Password,
        entity.ResourceId,
        entity.Venue,
        entity.VenueUser,
        entity.DayOfWeek,
        entity.Hour);

    private static PreparedBooking Prepared(BookingUserConfig user, BookingSlot slot) => new(
        user,
        slot,
        "{\"booking\":{\"spaces\":[\"" + user.ResourceId + "\"]}}",
        "X-Skedda-RequestVerificationCookie=csrf; X-Skedda-ApplicationCookie=app",
        "token",
        "csrf",
        "app");

    private sealed class SingleClientHttpClientFactory : IHttpClientFactory
    {
        private readonly string _baseUrl;
        public SingleClientHttpClientFactory(string baseUrl) => _baseUrl = baseUrl;
        public HttpClient CreateClient(string name) => new() { BaseAddress = new Uri(_baseUrl) };
    }

    private static BookingCancellationLink BookingLink(
        DateTimeOffset? cancelledAtUtc,
        string? reminder24hJobId,
        string? reminder2hJobId) => new(
        BasicDomainConfig(),
        new BookingSlot(new DateTimeOffset(2030, 6, 15, 10, 0, 0, TimeSpan.Zero)),
        5,
        10,
        "skedda-1",
        DateTimeOffset.UtcNow,
        cancelledAtUtc,
        null,
        null,
        reminder24hJobId,
        reminder2hJobId);

    private static DefaultHttpContext NewHttp()
    {
        var http = new DefaultHttpContext();
        http.RequestServices = new NullServiceProvider();
        return http;
    }

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTimeOffset utcNow) => UtcNow = utcNow;
        public DateTimeOffset UtcNow { get; }
    }

    private sealed class RecordingScheduler : IBookingScheduler
    {
        public PreparedBooking? PreciseBooking { get; private set; }
        public int? FallbackUserConfigId { get; private set; }
        public DateTimeOffset? FallbackStartTime { get; private set; }
        public BookingUserConfig? RecurringUserConfig { get; private set; }

        public void SchedulePreciseBooking(PreparedBooking booking) => PreciseBooking = booking;

        public void ScheduleFallback(int userConfigId, DateTimeOffset startTime, DateTimeOffset runAt)
        {
            FallbackUserConfigId = userConfigId;
            FallbackStartTime = startTime;
        }

        public string ScheduleAttendanceCheck(long chatId, int telegramMessageId, DateTimeOffset slotStartUtc, string reminderType, DateTimeOffset runAtUtc) => string.Empty;

        public void DeleteAttendanceCheck(string jobId) { }

        public void ScheduleRecurringPreparation(BookingUserConfig userConfig) => RecurringUserConfig = userConfig;
    }

    private sealed class NullServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _fn;
        public DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> fn) => _fn = fn;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _fn(request, cancellationToken);
    }

    private sealed class FakeSkeddaServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly Queue<(string method, string path, Func<HttpListenerContext, string> response)> _responses = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public string BaseUrl { get; }

        public FakeSkeddaServer()
        {
            var port = GetFreePort();
            BaseUrl = $"http://127.0.0.1:{port}";
            _listener = new HttpListener();
            _listener.Prefixes.Add(BaseUrl + "/");
            _listener.Start();
            _loop = Task.Run(LoopAsync);
        }

        public void Enqueue(HttpMethod method, string path, Func<HttpListenerContext, string> response)
            => _responses.Enqueue((method.Method, path, response));

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext? ctx = null;
                try
                {
                    ctx = await _listener.GetContextAsync();
                    if (_responses.Count == 0)
                    {
                        ctx.Response.StatusCode = 500;
                        await WriteAsync(ctx.Response, "No response configured");
                        continue;
                    }

                    var expected = _responses.Dequeue();
                    Assert.Equal(expected.method, ctx.Request.HttpMethod);
                    Assert.Equal(expected.path, ctx.Request.Url!.AbsolutePath);
                    await WriteAsync(ctx.Response, expected.response(ctx));
                }
                catch when (_cts.IsCancellationRequested)
                {
                    return;
                }
                catch
                {
                    if (ctx != null)
                    {
                        ctx.Response.StatusCode = 500;
                        ctx.Response.Close();
                    }

                    throw;
                }
            }
        }

        private static async Task WriteAsync(HttpListenerResponse response, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            response.ContentType = "text/html";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
            response.Close();
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _listener.Close();
            try { _loop.GetAwaiter().GetResult(); } catch { }
            _cts.Dispose();
        }

        private static int GetFreePort()
        {
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
