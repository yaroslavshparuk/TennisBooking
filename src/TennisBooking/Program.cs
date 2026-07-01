using System.Net;
using System.Net.Sockets;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Npgsql;
using TennisBooking.Auth;
using TennisBooking.Application.Abstractions;
using TennisBooking.Application.Booking;
using TennisBooking.DAL;
using TennisBooking.HealthChecks;
using TennisBooking.Infrastructure.Persistence;
using TennisBooking.Infrastructure.Scheduling;
using TennisBooking.Infrastructure.Skedda;
using TennisBooking.Infrastructure.Telegram;
using TennisBooking.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables();
var skeddaConfig = builder.Configuration.GetSection("SkeddaConfig");
var telegramConfig = builder.Configuration.GetSection("Telegram");
builder.Services.Configure<SkeddaOptions>(skeddaConfig);
builder.Services.Configure<TelegramOptions>(telegramConfig);
builder.Services.AddHttpClient<TelegramNotificationSender>();

// Pooled, keep-alive HttpClient for the latency-critical Skedda booking POST.
// IHttpClientFactory caches a single SocketsHttpHandler for this named client and reuses it
// across every CreateClient call and across DI scopes, so the TCP+TLS connection established
// during the pre-warm window (see PreciseBookingScheduler) is still open at the target instant
// instead of a fresh handshake being paid on the hot path.
builder.Services.AddHttpClient(SkeddaClient.SkeddaHttpClientName, (sp, client) =>
    {
        var opts = sp.GetRequiredService<IOptions<SkeddaOptions>>().Value;
        client.BaseAddress = new Uri(opts.ApiBaseUrl);
        // Opportunistically negotiate HTTP/2 via ALPN; silently falls back to HTTP/1.1.
        client.DefaultRequestVersion = HttpVersion.Version20;
        client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        // We attach the session Cookie header manually on every request (BuildCookieHeader).
        // Disable the handler's own CookieContainer so a Set-Cookie received on a pooled
        // connection (e.g. during the warm-up GET) can never be auto-appended to a later
        // request's manual Cookie header and produce a duplicate/conflicting cookie.
        UseCookies = false,
        // Keep connections warm for the process lifetime, recycling periodically so we still
        // pick up Front Door DNS/IP changes.
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
        // Headroom so concurrent same-window bookings (one warm-up + POST each) never queue for a
        // connection under an HTTP/1.1 fallback; free under HTTP/2 where a connection multiplexes.
        MaxConnectionsPerServer = 10,
        EnableMultipleHttp2Connections = true,
        // Disable Nagle's algorithm (TCP_NODELAY) so the small booking POST is flushed immediately.
        ConnectCallback = async (context, cancellationToken) =>
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(context.DnsEndPoint, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    });

var connString = builder.Configuration.GetConnectionString("Default")
                 ?? throw new InvalidOperationException("Connection string 'Default' not found.");
builder.Services.AddSingleton(new NpgsqlDataSourceBuilder(connString).Build());
builder.Services.AddDbContext<ApplicationDbContext>(opt =>
    opt.UseNpgsql(connString, pg => pg.MigrationsAssembly(typeof(Program).Assembly.GetName().Name)));

builder.Services.AddHangfire(config => config
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(connString,
        new PostgreSqlStorageOptions { SchemaName = "hangfire", QueuePollInterval = TimeSpan.FromSeconds(1) })
    .UseFilter(new AutomaticRetryAttribute
    {
        Attempts        = 2,
        DelaysInSeconds = new[] { 2, 5 }
    })
);
builder.Services.AddHangfireServer(options =>
{
    options.WorkerCount = 2;
    options.SchedulePollingInterval = TimeSpan.FromSeconds(1);
});
builder.Services.AddSingleton<PreciseBookingScheduler>();
builder.Services.AddSingleton<IPreciseBookingScheduler>(sp => sp.GetRequiredService<PreciseBookingScheduler>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<PreciseBookingScheduler>());
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IBookingDeduplicationStore, InMemoryBookingDeduplicationStore>();
builder.Services.AddScoped<IUserBookingConfigRepository, UserBookingConfigRepository>();
builder.Services.AddScoped<IBookingCancellationLinkRepository, BookingCancellationLinkRepository>();
builder.Services.AddScoped<ITelegramPollingStateRepository, TelegramPollingStateRepository>();
builder.Services.AddScoped<ITelegramChatRepository, TelegramChatRepository>();
// Singleton: SkeddaClient now holds only IHttpClientFactory + IOptions + ILogger (all singleton-safe)
// and no per-request mutable state, so a single instance avoids per-scope allocation on the hot path.
builder.Services.AddSingleton<ISkeddaClient, SkeddaClient>();
builder.Services.AddScoped<INotificationSender, TelegramNotificationSender>();
builder.Services.AddHostedService<TelegramLongPollingService>();
builder.Services.AddScoped<IBookingScheduler, HangfireBookingScheduler>();
builder.Services.AddScoped<PrepareBookingUseCase>();
builder.Services.AddScoped<PrepareBookingForConfigUseCase>();
builder.Services.AddScoped<ExecuteBookingUseCase>();
builder.Services.AddScoped<BookingFallbackUseCase>();
builder.Services.AddScoped<AttendanceReminderUseCase>();
builder.Services.AddScoped<CancelBookingUseCase>();
builder.Services.AddScoped<UpdateBookingScheduleUseCase>();
builder.Services.AddScoped<ScheduleBookingsUseCase>();
builder.Services.AddControllersWithViews();
builder.Services.AddHealthChecks()
    .AddCheck(
        "postgres",
        new NpgsqlHealthCheck(connString),
        failureStatus: HealthStatus.Degraded,
        tags: new[] { "db", "sql", "postgres" }
    ).AddCheck<PreparationHealthCheck>(
         "preparation",
         failureStatus: HealthStatus.Unhealthy,
         tags: new[] { "service", "custom" });;

var resourceBuilder = ResourceBuilder.CreateDefault()
    .AddService(serviceName: builder.Configuration["OpenTelemetry:ServiceName"], serviceVersion: "1.0.0")
    .AddTelemetrySdk()
    .AddEnvironmentVariableDetector();

var otlpEndpoint = new Uri(builder.Configuration["OpenTelemetry:Endpoint"]);

var otelBuilder = builder.Services.AddOpenTelemetry();
otelBuilder.WithTracing(tracing =>
{
    tracing
        .SetResourceBuilder(resourceBuilder)
        .AddAspNetCoreInstrumentation(options =>
        {
            options.RecordException = true;
        })
        .AddHttpClientInstrumentation(options =>
        {
            options.RecordException = true;
        })
        .AddSource("TennisBooking.*")
        .AddSource("Hangfire.*")
        .SetSampler(new TraceIdRatioBasedSampler(1.0))
        .AddOtlpExporter(o => o.Endpoint = otlpEndpoint);
});
otelBuilder.WithMetrics(metrics =>
{
    metrics
        .SetResourceBuilder(resourceBuilder)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddProcessInstrumentation()
        .AddRuntimeInstrumentation()
        .AddMeter("System.Runtime")
        .AddMeter("TennisBooking.*")
        .AddMeter("Hangfire.*")
        .AddOtlpExporter(o => o.Endpoint = otlpEndpoint);
});

builder.Logging.ClearProviders();
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Logging.AddConsole();
builder.Logging.AddOpenTelemetry(logging =>
{
    logging
        .SetResourceBuilder(resourceBuilder)
        .AddOtlpExporter(o => o.Endpoint = otlpEndpoint);
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    await db.Database.MigrateAsync();
}

app.UseStaticFiles();
app.UseRouting();
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = new[]
    {
        new HangfireBasicAuthFilter(builder.Configuration["Hangfire:DashboardUser"], builder.Configuration["Hangfire:DashboardPass"])
    }
});
app.UseEndpoints(endpoints => {
    endpoints.MapControllerRoute(
        name: "default",
        pattern: "{controller=Settings}/{action=Index}/{id?}");
    endpoints.MapControllers();
    endpoints.MapHealthChecks("/health", new HealthCheckOptions
    {
        Predicate = _ => false
    });
    endpoints.MapHealthChecks("/health/ready");
});
using (var scope = app.Services.CreateScope()) {
    var scheduler = scope.ServiceProvider.GetRequiredService<ScheduleBookingsUseCase>();
    await scheduler.ExecuteAsync(CancellationToken.None);
}

app.Run();
