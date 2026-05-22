using System.Diagnostics;
using System.Net;
using System.Text;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using TennisBooking.Application.Abstractions;
using TennisBooking.Application.Booking;
using TennisBooking.DAL;
using TennisBooking.DAL.Models;
using TennisBooking.Domain.Booking;
using TennisBooking.Infrastructure.Persistence;
using TennisBooking.Infrastructure.Scheduling;
using TennisBooking.Infrastructure.Skedda;
using TennisBooking.Options;
using Testcontainers.PostgreSql;
using Xunit;

namespace TennisBooking.Tests.Integration;

public sealed class BookingPostgresIntegrationTests
{
    [DockerFact]
    public async Task PrepareBooking_PersistsScheduledFallback_WithMinimalArgumentsInHangfirePostgres()
    {
        await using var postgres = await StartPostgresAsync();
        var connectionString = postgres.GetConnectionString();

        await using var db = await CreateMigratedDbAsync(connectionString);
        using var skedda = new FakeSkeddaServer();
        EnqueuePreparationResponses(skedda);

        var storage = new PostgreSqlStorage(connectionString, _ => { }, new PostgreSqlStorageOptions
        {
            SchemaName = "hangfire",
            QueuePollInterval = TimeSpan.FromSeconds(1)
        });
        var backgroundJobs = new BackgroundJobClient(storage);
        var scheduler = new HangfireBookingScheduler(backgroundJobs, Mock.Of<IPreciseBookingScheduler>());
        var service = new PrepareBookingUseCase(
            new SkeddaClient(Microsoft.Extensions.Options.Options.Create(new SkeddaOptions { ApiBaseUrl = skedda.BaseUrl })),
            scheduler,
            new FixedClock(new DateTimeOffset(2026, 5, 19, 12, 0, 0, TimeSpan.Zero)));

        var userConfig = NewConfig("postgres-schedule");
        db.UserConfigs.Add(userConfig);
        await db.SaveChangesAsync();

        await service.ExecuteAsync(ToDomain(userConfig), scheduleBookingJob: true, CancellationToken.None);

        var persistedJob = await ReadLatestHangfireJobAsync(connectionString);
        Assert.Contains(nameof(BookingFallbackUseCase.ExecuteAsync), persistedJob.InvocationData);
        Assert.Contains(userConfig.Id.ToString(), persistedJob.Arguments);
        Assert.Contains("Scheduled", persistedJob.StateData);

        Assert.DoesNotContain(nameof(PreparedBooking), persistedJob.Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", persistedJob.Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(userConfig.Password, persistedJob.Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("csrf-cookie", persistedJob.Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("app-cookie", persistedJob.Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token-1", persistedJob.Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token-2", persistedJob.Arguments, StringComparison.OrdinalIgnoreCase);
    }

    [DockerFact]
    public async Task BookingFallback_LoadsUserConfigFromRealPostgres_AndBooksOnce()
    {
        await using var postgres = await StartPostgresAsync();
        var connectionString = postgres.GetConnectionString();

        await using var db = await CreateMigratedDbAsync(connectionString);
        using var skedda = new FakeSkeddaServer();
        var bookingPosts = 0;
        EnqueuePreparationResponses(skedda);
        skedda.Enqueue(HttpMethod.Post, "/bookings", ctx =>
        {
            bookingPosts++;
            ctx.Response.StatusCode = 200;
            return """{"booking":{"id":"1"}}""";
        });

        var userConfig = NewConfig("postgres-fallback");
        db.UserConfigs.Add(userConfig);
        await db.SaveChangesAsync();

        var skeddaClient = new SkeddaClient(Microsoft.Extensions.Options.Options.Create(new SkeddaOptions { ApiBaseUrl = skedda.BaseUrl }));
        var notification = new Mock<INotificationSender>();
        notification.Setup(x => x.NotifyBookingSucceededAsync(It.IsAny<BookingUserConfig>(), It.IsAny<BookingSlot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TelegramNotificationResult(5, 10));
        var execute = new ExecuteBookingUseCase(
            skeddaClient,
            notification.Object,
            new InMemoryBookingDeduplicationStore(),
            new BookingCancellationLinkRepository(db));
        var fallback = new BookingFallbackUseCase(new UserBookingConfigRepository(db), skeddaClient, execute);
        var startTime = new DateTimeOffset(2030, 1, 1, userConfig.Hour, 0, 0, TimeSpan.Zero);

        await fallback.ExecuteAsync(userConfig.Id, startTime, CancellationToken.None);

        Assert.Equal(1, bookingPosts);
    }

    [DockerFact]
    public async Task BookingFallback_DoesNotDoublePost_WhenPreciseBookingAlreadyPostedSameSlot()
    {
        await using var postgres = await StartPostgresAsync();
        var connectionString = postgres.GetConnectionString();

        await using var db = await CreateMigratedDbAsync(connectionString);
        using var skedda = new FakeSkeddaServer();
        var bookingPosts = 0;

        skedda.Enqueue(HttpMethod.Post, "/bookings", ctx =>
        {
            bookingPosts++;
            ctx.Response.StatusCode = 200;
            return """{"booking":{"id":"1"}}""";
        });
        EnqueuePreparationResponses(skedda);

        var userConfig = NewConfig("postgres-dedupe");
        db.UserConfigs.Add(userConfig);
        await db.SaveChangesAsync();

        var notification = new Mock<INotificationSender>();
        notification.Setup(x => x.NotifyBookingSucceededAsync(It.IsAny<BookingUserConfig>(), It.IsAny<BookingSlot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TelegramNotificationResult(5, 10));
        var skeddaClient = new SkeddaClient(Microsoft.Extensions.Options.Options.Create(new SkeddaOptions { ApiBaseUrl = skedda.BaseUrl }));
        var dedupe = new InMemoryBookingDeduplicationStore();
        var execute = new ExecuteBookingUseCase(
            skeddaClient,
            notification.Object,
            dedupe,
            new BookingCancellationLinkRepository(db));
        var fallback = new BookingFallbackUseCase(new UserBookingConfigRepository(db), skeddaClient, execute);
        var startTime = new DateTimeOffset(2030, 1, 1, userConfig.Hour, 0, 0, TimeSpan.Zero);

        await execute.ExecuteAsync(new PreparedBooking(
            ToDomain(userConfig),
            new BookingSlot(startTime),
            new { booking = new { spaces = new[] { userConfig.ResourceId } } },
            "token",
            "csrf",
            "app"), CancellationToken.None);
        await fallback.ExecuteAsync(userConfig.Id, startTime, CancellationToken.None);

        Assert.Equal(1, bookingPosts);
    }

    private static async Task<PostgreSqlContainer> StartPostgresAsync()
    {
        var postgres = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("tennisbooking_tests")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        await postgres.StartAsync();
        return postgres;
    }

    private static async Task<ApplicationDbContext> CreateMigratedDbAsync(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString, pg => pg.MigrationsAssembly("TennisBooking"))
            .Options;
        var db = new ApplicationDbContext(options);
        await db.Database.MigrateAsync();
        return db;
    }

    private static UserConfig NewConfig(string usernamePrefix) => new()
    {
        Username = $"{usernamePrefix}-{Guid.NewGuid():N}",
        Password = $"secret-password-{Guid.NewGuid():N}",
        ResourceId = $"resource-{Guid.NewGuid():N}",
        Venue = "venue",
        VenueUser = "venue-user",
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

    private static void EnqueuePreparationResponses(FakeSkeddaServer skedda)
    {
        skedda.Enqueue(HttpMethod.Get, "/account/login", ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.Headers.Add("Set-Cookie", "X-Skedda-RequestVerificationCookie=csrf-cookie; Path=/");
            return "<input name=\"__RequestVerificationToken\" type=\"hidden\" value=\"token-1\" />";
        });
        skedda.Enqueue(HttpMethod.Post, "/logins", ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.Headers.Add("Set-Cookie", "X-Skedda-ApplicationCookie=app-cookie; Path=/");
            return "{}";
        });
        skedda.Enqueue(HttpMethod.Get, "/booking", ctx =>
        {
            ctx.Response.StatusCode = 200;
            return "<input name=\"__RequestVerificationToken\" type=\"hidden\" value=\"token-2\" />";
        });
    }

    private static async Task<(string InvocationData, string Arguments, string StateData)> ReadLatestHangfireJobAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand("""
            select j.invocationdata, j.arguments, coalesce(s.name, '') || ' ' || coalesce(s.data::text, '') as statedata
            from hangfire.job j
            left join hangfire.state s on s.jobid = j.id
            order by j.id desc, s.id desc
            limit 1;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "Expected Hangfire to persist one scheduled job.");
        return (reader.GetString(0), reader.GetString(1), reader.GetString(2));
    }

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTimeOffset utcNow) => UtcNow = utcNow;
        public DateTimeOffset UtcNow { get; }
    }

    private sealed class DockerFactAttribute : FactAttribute
    {
        public DockerFactAttribute()
        {
            if (!DockerAvailable())
                Skip = "Docker is not available; skipping Testcontainers PostgreSQL integration test.";
        }

        private static bool DockerAvailable()
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = "version --format {{.Server.Version}}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                });
                if (process == null)
                    return false;

                return process.WaitForExit(5_000) && process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
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
