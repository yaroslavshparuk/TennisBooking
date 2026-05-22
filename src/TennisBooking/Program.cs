using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
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

var connString = builder.Configuration.GetConnectionString("Default")
                 ?? throw new InvalidOperationException("Connection string 'Default' not found.");
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
builder.Services.AddScoped<ISkeddaClient, SkeddaClient>();
builder.Services.AddScoped<INotificationSender, TelegramNotificationSender>();
builder.Services.AddHostedService<TelegramLongPollingService>();
builder.Services.AddScoped<IBookingScheduler, HangfireBookingScheduler>();
builder.Services.AddScoped<PrepareBookingUseCase>();
builder.Services.AddScoped<PrepareBookingForConfigUseCase>();
builder.Services.AddScoped<ExecuteBookingUseCase>();
builder.Services.AddScoped<BookingFallbackUseCase>();
builder.Services.AddScoped<ScheduleBookingsUseCase>();
builder.Services.AddControllers();
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

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    await db.Database.MigrateAsync();
}

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
