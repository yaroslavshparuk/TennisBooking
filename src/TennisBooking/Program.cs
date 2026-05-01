using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Collections.Concurrent;
using TennisBooking.Auth;
using TennisBooking.DAL;
using TennisBooking.HealthChecks;
using TennisBooking.Models;
using TennisBooking.Options;
using TennisBooking.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables();
var skeddaConfig = builder.Configuration.GetSection("SkeddaConfig");
builder.Services.Configure<SkeddaOptions>(skeddaConfig);
builder.Services.AddHttpClient<BookingService>(client => {
    var opts = skeddaConfig.Get<SkeddaOptions>();
    client.BaseAddress = new Uri(opts.ApiBaseUrl);
});
builder.Services.AddHttpClient<TelegramService>();

var connString = builder.Configuration.GetConnectionString("Default")
                 ?? throw new InvalidOperationException("Connection string 'Default' not found.");
builder.Services.AddDbContext<ApplicationDbContext>(opt =>
    opt.UseNpgsql(connString));

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
builder.Services.AddScoped<BookingService>();
builder.Services.AddScoped<TelegramService>();
builder.Services.AddControllers();
builder.Services.AddHealthChecks()
    .AddNpgSql(
        builder.Configuration.GetConnectionString("Default"),
        name: "postgres",
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
        .AddRuntimeInstrumentation()
        .AddMeter("TennisBooking.*")
        .AddMeter("Hangfire.*")
        .AddOtlpExporter(o => o.Endpoint = otlpEndpoint);
});

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddOpenTelemetry(logging =>
{
    logging
        .SetResourceBuilder(resourceBuilder)
        .AddOtlpExporter(o => o.Endpoint = otlpEndpoint);
});

var app = builder.Build();

app.UseRouting();
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = new[]
    {
        new HangfireBasicAuthFilter(builder.Configuration["Hangfire:DashboardUser"], builder.Configuration["Hangfire:DashboardPass"])
    }
});
app.UseEndpoints(endpoints => {
    endpoints.MapControllers();
    endpoints.MapHangfireDashboard();
    endpoints.MapHealthChecks("/health");
});
using (var scope = app.Services.CreateScope()) {
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var configs = db.UserConfigs.AsNoTracking().ToList();
    foreach (var cfg in configs) {
        RecurringJob.AddOrUpdate<BookingService>(
            cfg.Username,
            x => x.Preparation(cfg, true, CancellationToken.None),
            Cron.Weekly(cfg.DayOfWeek, cfg.Hour - 1, 59),
            TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv")
        );
    }
}

app.Run();

namespace TennisBooking.Services
{
public interface IPreciseBookingScheduler
{
    void ScheduleBooking(BookingInfo bookingInfo);
}

public sealed class PreciseBookingScheduler : IPreciseBookingScheduler, IHostedService, IDisposable
{
    private static readonly TimeSpan WarmupBeforeTarget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PrecisionStep = TimeSpan.FromMilliseconds(20);

    private readonly ConcurrentDictionary<string, byte> _scheduled = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PreciseBookingScheduler> _logger;
    private readonly CancellationTokenSource _stoppingCts = new();

    public PreciseBookingScheduler(IServiceScopeFactory scopeFactory, ILogger<PreciseBookingScheduler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stoppingCts.Cancel();
        return Task.CompletedTask;
    }

    public void ScheduleBooking(BookingInfo bookingInfo)
    {
        if (bookingInfo == null)
            return;

        var key = BuildKey(bookingInfo);
        if (!_scheduled.TryAdd(key, 0))
        {
            _logger.LogInformation("Precise booking timer already scheduled for {Key}", key);
            return;
        }

        _ = Task.Run(() => RunPreciseTimerAsync(bookingInfo, key, _stoppingCts.Token));
    }

    private async Task RunPreciseTimerAsync(BookingInfo bookingInfo, string key, CancellationToken token)
    {
        try
        {
            var targetTime = bookingInfo.StartTime.AddDays(-14).ToUniversalTime();
            var warmupTime = targetTime - WarmupBeforeTarget;
            var now = DateTimeOffset.UtcNow;

            if (warmupTime > now)
                await Task.Delay(warmupTime - now, token);

            while (DateTimeOffset.UtcNow < targetTime)
            {
                var remaining = targetTime - DateTimeOffset.UtcNow;
                var delay = remaining > PrecisionStep ? PrecisionStep : remaining;
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, token);
            }

            using var scope = _scopeFactory.CreateScope();
            var bookingService = scope.ServiceProvider.GetRequiredService<BookingService>();
            await bookingService.Booking(bookingInfo, token);
            _logger.LogInformation("Precise timer triggered booking for {Username} at {Target}", bookingInfo.UserConfig.Username, targetTime);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Precise booking timer cancelled for {Key}", key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Precise booking timer failed for {Key}", key);
        }
        finally
        {
            _scheduled.TryRemove(key, out _);
        }
    }

    private static string BuildKey(BookingInfo bookingInfo)
        => $"{bookingInfo.UserConfig.Username}:{bookingInfo.StartTime.ToUnixTimeMilliseconds()}";

    public void Dispose()
    {
        _stoppingCts.Cancel();
        _stoppingCts.Dispose();
    }
}
}
