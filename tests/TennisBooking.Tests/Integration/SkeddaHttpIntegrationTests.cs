using System.Net;
using System.Runtime.ExceptionServices;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TennisBooking.Application.Abstractions;
using TennisBooking.Application.Booking;
using TennisBooking.Domain.Booking;
using TennisBooking.Infrastructure.Skedda;
using TennisBooking.Options;
using Xunit;

namespace TennisBooking.Tests.Integration;

/// <summary>
/// SkeddaClient HTTP integration tests without containers or live internet:
/// session/prepare/cancel flows run against a loopback HttpListener fake,
/// pipe-level book/warmup flows run against stubbed IHttpClientFactory handlers.
/// </summary>
public sealed class SkeddaHttpIntegrationTests
{
    private static readonly BookingUserConfig Config = new(
        1, "court-user", "s3cret", "resource-9", "Galaktyka", "venue-user-1",
        DayOfWeek.Monday, 10);

    private static readonly BookingSlot Slot =
        new(new DateTimeOffset(2030, 6, 1, 10, 0, 0, TimeSpan.Zero));

    private static SkeddaClient PipeClient(Func<HttpRequestMessage, HttpResponseMessage> responder, string baseUrl = "http://127.0.0.1:9")
        => new(
            new StubFactory(responder, baseUrl),
            Microsoft.Extensions.Options.Options.Create(new SkeddaOptions { ApiBaseUrl = baseUrl }),
            NullLogger<SkeddaClient>.Instance);

    private static SkeddaClient TokenAwarePipeClient(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder,
        string baseUrl = "http://127.0.0.1:9")
        => new(
            new TokenStubFactory(responder, baseUrl),
            Microsoft.Extensions.Options.Options.Create(new SkeddaOptions { ApiBaseUrl = baseUrl }),
            NullLogger<SkeddaClient>.Instance);

    // ---- PrepareBookingAsync over loopback HTTP ----

    [Fact]
    public async Task PrepareBooking_FullSessionFlow_ReturnsCookiesAndBody()
    {
        using var server = new LoopbackSkeddaServer();
        var loginPosts = 0;
        server.Enqueue(HttpMethod.Get, "/account/login", ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.Headers.Add("Set-Cookie", "X-Skedda-RequestVerificationCookie=csrf-cookie-1; Path=/");
            return """<html><input name="__RequestVerificationToken" type="hidden" value="token-1" /></html>""";
        });
        server.Enqueue(HttpMethod.Post, "/logins", ctx =>
        {
            loginPosts++;
            Assert.Equal("token-1", ctx.Request.Headers["x-skedda-requestverificationtoken"]);
            Assert.Contains("X-Skedda-RequestVerificationCookie=csrf-cookie-1", ctx.Request.Headers["Cookie"]);
            ctx.Response.StatusCode = 200;
            ctx.Response.Headers.Add("Set-Cookie", "X-Skedda-ApplicationCookie=app-cookie-1; Path=/");
            return "{}";
        });
        server.Enqueue(HttpMethod.Get, "/booking", _ =>
        {
            return """<html><input name="__RequestVerificationToken" type="hidden" value="token-2" /></html>""";
        });
        var client = new SkeddaClient(
            new NeverCalledFactory(),
            Microsoft.Extensions.Options.Options.Create(new SkeddaOptions { ApiBaseUrl = server.BaseUrl }),
            NullLogger<SkeddaClient>.Instance);

        var prepared = await client.PrepareBookingAsync(Config, Slot, CancellationToken.None);

        Assert.Equal(1, loginPosts);
        Assert.Equal("token-2", prepared.RequestVerificationToken);
        Assert.Equal("csrf-cookie-1", prepared.CsrfCookie);
        Assert.Equal("app-cookie-1", prepared.ApplicationCookie);
        Assert.Equal(
            "X-Skedda-RequestVerificationCookie=csrf-cookie-1; X-Skedda-ApplicationCookie=app-cookie-1",
            prepared.CookieHeader);
        Assert.Contains("resource-9", prepared.BodyJson);
        Assert.Contains("Galaktyka", prepared.BodyJson);
        Assert.Contains("2030-06-01T10:00:00", prepared.BodyJson);
        Assert.Same(Config, prepared.UserConfig);
        Assert.Equal(Slot.StartTime, prepared.Slot.StartTime);
        server.ThrowIfBackgroundFailed();
    }

    [Fact]
    public async Task PrepareBooking_LoginPageWithoutToken_Throws()
    {
        using var server = new LoopbackSkeddaServer();
        server.Enqueue(HttpMethod.Get, "/account/login", _ => "<html>no token here</html>");
        var client = new SkeddaClient(
            new NeverCalledFactory(),
            Microsoft.Extensions.Options.Options.Create(new SkeddaOptions { ApiBaseUrl = server.BaseUrl }),
            NullLogger<SkeddaClient>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.PrepareBookingAsync(Config, Slot, CancellationToken.None));
        server.ThrowIfBackgroundFailed();
    }

    // ---- BookAsync over stubbed pipes ----

    [Fact]
    public async Task Book_Success_SendsCsrfAndCookieHeaders_AndParsesId()
    {
        string? csrf = null;
        string? cookie = null;
        string? path = null;
        var client = PipeClient(req =>
        {
            csrf = req.Headers.TryGetValues("x-skedda-requestverificationtoken", out var v) ? string.Join(",", v) : null;
            cookie = req.Headers.TryGetValues("Cookie", out var c) ? string.Join(",", c) : null;
            path = req.RequestUri!.AbsolutePath;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"booking":{"id":"bk-123"}}""", Encoding.UTF8, "application/json")
            };
        });
        var prepared = new PreparedBooking(Config, Slot, """{"booking":{}}""", "X-Skedda-RequestVerificationCookie=c; X-Skedda-ApplicationCookie=a", "tok", "c", "a");

        var result = await client.BookAsync(prepared, pipeId: 0, CancellationToken.None);

        Assert.Equal("bk-123", result.BookingId);
        Assert.Equal("/bookings", path);
        Assert.Equal("tok", csrf);
        Assert.Equal("X-Skedda-RequestVerificationCookie=c; X-Skedda-ApplicationCookie=a", cookie);
    }

    [Fact]
    public async Task Book_Conflict_ThrowsRejected_WithStatusAndBody()
    {
        var client = PipeClient(_ => new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent("slot taken", Encoding.UTF8, "text/plain")
        });
        var prepared = SamplePrepared();

        var ex = await Assert.ThrowsAsync<SkeddaBookingRejectedException>(
            () => client.BookAsync(prepared, 0, CancellationToken.None));
        Assert.Equal(409, ex.StatusCode);
        Assert.Contains("slot taken", ex.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task Book_NonRejectionStatuses_ThrowHttpRequestException(HttpStatusCode status)
    {
        var client = PipeClient(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent("boom", Encoding.UTF8, "text/plain")
        });

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.BookAsync(SamplePrepared(), 0, CancellationToken.None));
    }

    [Fact]
    public async Task Book_MissingBookingId_ThrowsInvalidOperation()
    {
        var client = PipeClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"booking":{}}""", Encoding.UTF8, "application/json")
        });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.BookAsync(SamplePrepared(), 0, CancellationToken.None));
    }

    [Fact]
    public async Task Book_UnregisteredPipe_ThrowsInvalidOperation()
    {
        var client = new SkeddaClient(
            new BareFactory(),
            Microsoft.Extensions.Options.Options.Create(new SkeddaOptions { ApiBaseUrl = "http://127.0.0.1:9" }),
            NullLogger<SkeddaClient>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.BookAsync(SamplePrepared(), 3, CancellationToken.None));
        Assert.Contains("Skedda-3", ex.Message);
    }

    // ---- CancelAsync over loopback HTTP ----

    [Fact]
    public async Task Cancel_Success_SendsDeleteWithSessionCookies()
    {
        using var server = new LoopbackSkeddaServer();
        EnqueueLoginFlow(server);
        // The session (GET/POST/GET) runs over real loopback HTTP; the DELETE itself
        // goes through pipe 0 of IHttpClientFactory, so it is captured in the stub.
        string? deletePath = null;
        string? deleteCookie = null;
        string? deleteCsrf = null;
        var client = new SkeddaClient(
            new StubFactory(req =>
            {
                Assert.Equal(HttpMethod.Delete, req.Method);
                deletePath = req.RequestUri!.AbsolutePath;
                deleteCookie = req.Headers.TryGetValues("Cookie", out var c) ? string.Join(",", c) : null;
                deleteCsrf = req.Headers.TryGetValues("x-skedda-requestverificationtoken", out var t) ? string.Join(",", t) : null;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }, server.BaseUrl),
            Microsoft.Extensions.Options.Options.Create(new SkeddaOptions { ApiBaseUrl = server.BaseUrl }),
            NullLogger<SkeddaClient>.Instance);

        await client.CancelAsync(SamplePrepared(), "bk-9", CancellationToken.None);

        Assert.Equal("/bookings/bk-9", deletePath);
        Assert.Contains("X-Skedda-ApplicationCookie=app-cookie-1", deleteCookie);
        Assert.Equal("token-2", deleteCsrf);
        server.ThrowIfBackgroundFailed();
    }

    [Fact]
    public async Task Cancel_FailedDelete_ThrowsHttpRequestException()
    {
        using var server = new LoopbackSkeddaServer();
        EnqueueLoginFlow(server);
        var client = new SkeddaClient(
            new StubFactory(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("nope", Encoding.UTF8, "text/plain")
            }, server.BaseUrl),
            Microsoft.Extensions.Options.Options.Create(new SkeddaOptions { ApiBaseUrl = server.BaseUrl }),
            NullLogger<SkeddaClient>.Instance);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.CancelAsync(SamplePrepared(), "bk-9", CancellationToken.None));
        server.ThrowIfBackgroundFailed();
    }

    // ---- WarmupAsync over stubbed pipes ----

    [Fact]
    public async Task Warmup_Success_ReturnsEstablished_WithSkewEstimate()
    {
        var client = PipeClient(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK);
            resp.Headers.Date = new DateTimeOffset(2030, 1, 1, 0, 0, 5, TimeSpan.Zero);
            return resp;
        });

        var result = await client.WarmupAsync(SamplePrepared(), 0, CancellationToken.None);

        Assert.True(result.Established);
    }

    [Fact]
    public async Task Warmup_TransportFailure_ReturnsNotEstablished()
    {
        var client = PipeClient(_ => throw new HttpRequestException("connection refused"));

        var result = await client.WarmupAsync(SamplePrepared(), 0, CancellationToken.None);

        Assert.False(result.Established);
        Assert.Null(result.ClockSkew);
    }

    [Fact]
    public async Task Warmup_Cancellation_Propagates()
    {
        // Token-aware stub: proves the token actually flows into the send,
        // not just that WarmupAsync fails to swallow an unconditional throw.
        var client = TokenAwarePipeClient((_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.WarmupAsync(SamplePrepared(), 0, cts.Token));

        // Control: the same stub without cancellation establishes.
        var ok = await client.WarmupAsync(SamplePrepared(), 0, CancellationToken.None);
        Assert.True(ok.Established);
    }

    [Fact]
    public void EstimateClockSkew_SubtractsHalfRoundTrip()
    {
        var sentAt = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // Server stamped 10s after send with a 4s round trip -> +8s skew.
        var skew = SkeddaClient.EstimateClockSkew(sentAt.AddSeconds(10), sentAt, TimeSpan.FromSeconds(4));

        Assert.Equal(TimeSpan.FromSeconds(8), skew);
    }

    // ---- helpers ----

    private static PreparedBooking SamplePrepared() => new(
        Config, Slot, """{"booking":{}}""",
        "X-Skedda-RequestVerificationCookie=c; X-Skedda-ApplicationCookie=a",
        "tok", "c", "a");

    private static void EnqueueLoginFlow(LoopbackSkeddaServer server)
    {
        server.Enqueue(HttpMethod.Get, "/account/login", ctx =>
        {
            ctx.Response.Headers.Add("Set-Cookie", "X-Skedda-RequestVerificationCookie=csrf-cookie-1; Path=/");
            return """<html><input name="__RequestVerificationToken" type="hidden" value="token-1" /></html>""";
        });
        server.Enqueue(HttpMethod.Post, "/logins", ctx =>
        {
            ctx.Response.Headers.Add("Set-Cookie", "X-Skedda-ApplicationCookie=app-cookie-1; Path=/");
            return "{}";
        });
        server.Enqueue(HttpMethod.Get, "/booking", _ =>
            """<html><input name="__RequestVerificationToken" type="hidden" value="token-2" /></html>""");
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        private readonly string _baseUrl;
        public StubFactory(Func<HttpRequestMessage, HttpResponseMessage> responder, string baseUrl)
        {
            _responder = responder;
            _baseUrl = baseUrl;
        }
        public HttpClient CreateClient(string name) =>
            new(new FuncHandler(_responder)) { BaseAddress = new Uri(_baseUrl) };
    }

    private sealed class BareFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(); // no BaseAddress: unregistered pipe
    }

    private sealed class NeverCalledFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("pipe factory must not be used here");
    }

    private sealed class FuncHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public FuncHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_responder(request));
        }
    }

    private sealed class TokenStubFactory : IHttpClientFactory
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _responder;
        private readonly string _baseUrl;
        public TokenStubFactory(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder, string baseUrl)
        {
            _responder = responder;
            _baseUrl = baseUrl;
        }
        public HttpClient CreateClient(string name) =>
            new(new TokenFuncHandler(_responder)) { BaseAddress = new Uri(_baseUrl) };
    }

    private sealed class TokenFuncHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _responder;
        public TokenFuncHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder) => _responder = responder;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request, cancellationToken));
    }

    private sealed class LoopbackSkeddaServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly Queue<(string method, string path, Func<HttpListenerContext, string> respond)> _responses = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private Exception? _backgroundFailure;

        public string BaseUrl { get; }

        public LoopbackSkeddaServer()
        {
            var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            tcp.Stop();
            BaseUrl = $"http://127.0.0.1:{port}";
            _listener = new HttpListener();
            _listener.Prefixes.Add(BaseUrl + "/");
            _listener.Start();
            _loop = Task.Run(LoopAsync);
        }

        public void Enqueue(HttpMethod method, string path, Func<HttpListenerContext, string> respond)
            => _responses.Enqueue((method.Method, path, respond));

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
                    await WriteAsync(ctx.Response, expected.respond(ctx));
                }
                catch when (_cts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Server-thread asserts would otherwise surface as a confusing
                    // client-side connection failure; record and rethrow on the test thread.
                    Interlocked.Exchange(ref _backgroundFailure, ex);
                    try { ctx?.Response.Close(); } catch { }
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

        // Rethrows any server-thread assert failure on the calling (test) thread
        // with its original stack. Call at the end of loopback tests.
        public void ThrowIfBackgroundFailed()
        {
            if (Interlocked.CompareExchange(ref _backgroundFailure, null, null) is { } failure)
                ExceptionDispatchInfo.Capture(failure).Throw();
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _listener.Close();
            try { _loop.GetAwaiter().GetResult(); } catch { }
            _cts.Dispose();
        }
    }
}
