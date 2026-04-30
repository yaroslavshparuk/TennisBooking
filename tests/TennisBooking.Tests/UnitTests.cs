using System.Net;
using System.Text;
using Hangfire;
using Hangfire.Dashboard;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using TennisBooking.Auth;
using TennisBooking.DAL;
using TennisBooking.DAL.Models;
using TennisBooking.HealthChecks;
using TennisBooking.Models;
using TennisBooking.Options;
using TennisBooking.Services;
using Xunit;

namespace TennisBooking.Tests;

public class UnitTests
{
    [Fact]
    public async Task Preparation_WithNullUserConfig_DoesNotThrow()
    {
        var svc = CreateBookingService("http://127.0.0.1:65534", out _, out _);
        await svc.Preparation(null!, false, CancellationToken.None);
    }

    [Fact]
    public async Task Preparation_SchedulesJob_WhenRequested()
    {
        using var server = new FakeSkeddaServer();
        server.Enqueue(HttpMethod.Get, "/account/login", ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.Headers.Add("Set-Cookie", "X-Skedda-RequestVerificationCookie=csrf-cookie; Path=/");
            return "<input name=\"__RequestVerificationToken\" type=\"hidden\" value=\"token-1\" />";
        });
        server.Enqueue(HttpMethod.Post, "/logins", ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.Headers.Add("Set-Cookie", "X-Skedda-ApplicationCookie=app-cookie; Path=/");
            return "{}";
        });
        server.Enqueue(HttpMethod.Get, "/booking", ctx =>
        {
            ctx.Response.StatusCode = 200;
            return "<input name=\"__RequestVerificationToken\" type=\"hidden\" value=\"token-2\" />";
        });

        var bg = new Mock<IBackgroundJobClient>();
        bg.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns("job-id");
        var precise = new Mock<IPreciseBookingScheduler>();

        var svc = CreateBookingService(server.BaseUrl, out var telegramDb, out _, bg.Object, precise.Object);
        telegramDb.TelegramConfigs.Add(new TelegramConfig { BotToken = "t", ChatId = 1 });
        telegramDb.SaveChanges();

        var cfg = new UserConfig
        {
            Username = "u",
            Password = "p",
            ResourceId = "res",
            Venue = "venue",
            VenueUser = "venue-user",
            DayOfWeek = DayOfWeek.Monday,
            Hour = 10
        };

        Job? scheduledJob = null;
        bg.Invocations.Clear();
        bg.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>()))
            .Callback<Job, IState>((job, _) => scheduledJob = job)
            .Returns("job-id");

        await svc.Preparation(cfg, true, CancellationToken.None);

        bg.Verify(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Once);
        precise.Verify(x => x.ScheduleBooking(It.IsAny<BookingInfo>()), Times.Once);
        Assert.NotNull(scheduledJob);
        Assert.Equal(nameof(BookingService.BookingFallback), scheduledJob!.Method.Name);
        Assert.DoesNotContain(scheduledJob.Args, arg => arg is BookingInfo);
    }

    [Fact]
    public async Task Booking_DoesNotPostTwice_ForSameSlot()
    {
        using var skedda = new FakeSkeddaServer();
        var bookingPosts = 0;
        skedda.Enqueue(HttpMethod.Post, "/bookings", ctx =>
        {
            bookingPosts++;
            ctx.Response.StatusCode = 200;
            return "{}";
        });

        var svc = CreateBookingService(skedda.BaseUrl, out _, out _);
        var info = new BookingInfo
        {
            UserConfig = BasicConfig(),
            Body = new { },
            RequestVerificationToken = "t",
            CsrfCookie = "c",
            ApplicationCookie = "a",
            StartTime = new DateTimeOffset(2030, 1, 1, 10, 0, 0, TimeSpan.Zero)
        };

        await svc.Booking(info, CancellationToken.None);
        await svc.Booking(info, CancellationToken.None);

        Assert.Equal(1, bookingPosts);
    }

    [Fact]
    public async Task BookingFallback_LoadsConfigAndBooksWithoutSensitiveHangfireArgs()
    {
        using var server = new FakeSkeddaServer();
        server.Enqueue(HttpMethod.Get, "/account/login", ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.Headers.Add("Set-Cookie", "X-Skedda-RequestVerificationCookie=csrf-cookie; Path=/");
            return "<input name=\"__RequestVerificationToken\" type=\"hidden\" value=\"token-1\" />";
        });
        server.Enqueue(HttpMethod.Post, "/logins", ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.Headers.Add("Set-Cookie", "X-Skedda-ApplicationCookie=app-cookie; Path=/");
            return "{}";
        });
        server.Enqueue(HttpMethod.Get, "/booking", ctx =>
        {
            ctx.Response.StatusCode = 200;
            return "<input name=\"__RequestVerificationToken\" type=\"hidden\" value=\"token-2\" />";
        });
        server.Enqueue(HttpMethod.Post, "/bookings", ctx =>
        {
            ctx.Response.StatusCode = 200;
            return "{}";
        });

        var svc = CreateBookingService(server.BaseUrl, out var db, out _);
        var cfg = BasicConfig();
        cfg.Id = 42;
        db.UserConfigs.Add(cfg);
        db.SaveChanges();

        var startTime = new DateTimeOffset(2030, 1, 1, cfg.Hour, 0, 0, TimeSpan.Zero);
        await svc.BookingFallback(cfg.Id, startTime, CancellationToken.None);
    }

    [Fact]
    public async Task BookingFallback_Throws_WhenUserConfigMissing()
    {
        var svc = CreateBookingService("http://127.0.0.1:65534", out _, out _);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.BookingFallback(999, DateTimeOffset.UtcNow, CancellationToken.None));
    }

    [Fact]
    public async Task Preparation_Throws_WhenTokenMissing()
    {
        using var server = new FakeSkeddaServer();
        server.Enqueue(HttpMethod.Get, "/account/login", ctx =>
        {
            ctx.Response.StatusCode = 200;
            return "<html>no token</html>";
        });

        var svc = CreateBookingService(server.BaseUrl, out _, out _);
        var cfg = BasicConfig();
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.Preparation(cfg, false, CancellationToken.None));
    }

    [Fact]
    public async Task Booking_SendsTelegramNotification_OnSuccess()
    {
        using var skedda = new FakeSkeddaServer();
        skedda.Enqueue(HttpMethod.Post, "/bookings", ctx =>
        {
            ctx.Response.StatusCode = 200;
            return "{}";
        });

        var telegramCalls = 0;
        var telegramHandler = new DelegateHandler((req, _) =>
        {
            if (req.RequestUri!.Host.Contains("api.telegram.org")) telegramCalls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var dbOpts = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var db = new ApplicationDbContext(dbOpts);
        db.TelegramConfigs.Add(new TelegramConfig { BotToken = "abc", ChatId = 123 });
        db.SaveChanges();

        var tg = new TelegramService(new HttpClient(telegramHandler), db, NullLogger<TelegramService>.Instance);
        var svc = new BookingService(
            NullLogger<BookingService>.Instance,
            Microsoft.Extensions.Options.Options.Create(new SkeddaOptions { ApiBaseUrl = skedda.BaseUrl }),
            db,
            tg,
            Mock.Of<IBackgroundJobClient>(),
            Mock.Of<IPreciseBookingScheduler>());

        var info = new BookingInfo
        {
            UserConfig = BasicConfig(),
            Body = new { booking = new { x = 1 } },
            RequestVerificationToken = "token",
            CsrfCookie = "csrf",
            ApplicationCookie = "app",
            StartTime = DateTimeOffset.UtcNow
        };

        await svc.Booking(info, CancellationToken.None);
        Assert.Equal(1, telegramCalls);
    }

    [Fact]
    public async Task Booking_Throws_OnSkeddaFailure()
    {
        using var skedda = new FakeSkeddaServer();
        skedda.Enqueue(HttpMethod.Post, "/bookings", ctx =>
        {
            ctx.Response.StatusCode = 500;
            return "fail";
        });

        var svc = CreateBookingService(skedda.BaseUrl, out _, out _);
        await Assert.ThrowsAsync<HttpRequestException>(() => svc.Booking(new BookingInfo
        {
            UserConfig = BasicConfig(),
            Body = new { },
            RequestVerificationToken = "t",
            CsrfCookie = "c",
            ApplicationCookie = "a",
            StartTime = DateTimeOffset.UtcNow
        }, CancellationToken.None));
    }

    [Fact]
    public async Task Booking_NullInfo_DoesNotThrow()
    {
        var svc = CreateBookingService("http://127.0.0.1:65534", out _, out _);
        await svc.Booking(null!, CancellationToken.None);
    }

    [Fact]
    public async Task TelegramService_Notify_Works_And_HandlesMissingConfig()
    {
        var handler = new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var opts = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using var db = new ApplicationDbContext(opts);

        var svc = new TelegramService(new HttpClient(handler), db, NullLogger<TelegramService>.Instance);
        await svc.NotifyAsync("hello"); // missing config path (caught internally)

        db.TelegramConfigs.Add(new TelegramConfig { BotToken = "x", ChatId = 5 });
        db.SaveChanges();
        await svc.NotifyAsync("hello"); // success path
    }

    [Fact]
    public async Task PreparationHealthCheck_ReturnsHealthy_AndUnhealthy()
    {
        var opts = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using var db = new ApplicationDbContext(opts);

        var healthySvc = CreateBookingService("http://127.0.0.1:65534", out _, out _);
        var hc1 = new PreparationHealthCheck(healthySvc, db, NullLogger<PreparationHealthCheck>.Instance);
        var result1 = await hc1.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
        Assert.Equal(HealthStatus.Healthy, result1.Status);

        db.UserConfigs.Add(BasicConfig());
        db.SaveChanges();

        var badSvc = CreateBookingService("not-a-url", out _, out _);
        var hc2 = new PreparationHealthCheck(badSvc, db, NullLogger<PreparationHealthCheck>.Instance);
        var result2 = await hc2.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
        Assert.Equal(HealthStatus.Unhealthy, result2.Status);
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
        httpBadScheme.Request.Headers["Authorization"] = "Bearer abc";
        Assert.False(filter.Authorize(new AspNetCoreDashboardContext(storage, options, httpBadScheme)));

        var httpBadCreds = NewHttp();
        httpBadCreds.Request.Headers["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("user:nope"));
        Assert.False(filter.Authorize(new AspNetCoreDashboardContext(storage, options, httpBadCreds)));

        var httpBadBase64 = NewHttp();
        httpBadBase64.Request.Headers["Authorization"] = "Basic not-base64";
        Assert.False(filter.Authorize(new AspNetCoreDashboardContext(storage, options, httpBadBase64)));

        var httpGood = NewHttp();
        httpGood.Request.Headers["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("user:pass"));
        Assert.True(filter.Authorize(new AspNetCoreDashboardContext(storage, options, httpGood)));
    }

    [Fact]
    public void Model_And_Options_PropertyCoverage()
    {
        var u = BasicConfig();
        Assert.Equal("u", u.Username);

        var t = new TelegramConfig { Id = 1, BotToken = "token", ChatId = 42 };
        Assert.Equal(42, t.ChatId);

        var o = new SkeddaOptions { ApiBaseUrl = "http://localhost" };
        Assert.Contains("http", o.ApiBaseUrl);

        var bi = new BookingInfo
        {
            UserConfig = u,
            RequestVerificationToken = "r",
            CsrfCookie = "c",
            ApplicationCookie = "a",
            Body = new { n = 1 },
            StartTime = DateTimeOffset.UtcNow
        };
        Assert.NotNull(bi.Body);
    }

    private static UserConfig BasicConfig() => new()
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

    private static BookingService CreateBookingService(string apiBaseUrl, out ApplicationDbContext telegramDb, out IBackgroundJobClient bg, IBackgroundJobClient? bgOverride = null, IPreciseBookingScheduler? preciseOverride = null)
    {
        var tgHandler = new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var dbOpts = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        telegramDb = new ApplicationDbContext(dbOpts);
        var tg = new TelegramService(new HttpClient(tgHandler), telegramDb, NullLogger<TelegramService>.Instance);
        bg = bgOverride ?? Mock.Of<IBackgroundJobClient>();
        var precise = preciseOverride ?? Mock.Of<IPreciseBookingScheduler>();
        return new BookingService(
            NullLogger<BookingService>.Instance,
            Microsoft.Extensions.Options.Options.Create(new SkeddaOptions { ApiBaseUrl = apiBaseUrl }),
            telegramDb,
            tg,
            bg,
            precise);
    }

    private static DefaultHttpContext NewHttp()
    {
        var http = new DefaultHttpContext();
        http.RequestServices = new NullServiceProvider();
        return http;
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
                    var body = expected.response(ctx);
                    await WriteAsync(ctx.Response, body);
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
