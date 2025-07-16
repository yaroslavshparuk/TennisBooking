using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using TennisBooking.Auth;
using TennisBooking.DAL;
using TennisBooking.HealthChecks;
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
        new PostgreSqlStorageOptions { SchemaName = "hangfire", QueuePollInterval = TimeSpan.FromSeconds(5) })
    .UseFilter(new AutomaticRetryAttribute
    {
        Attempts        = 5,
        DelaysInSeconds = new[] { 1, 1, 1, 1, 2 }
    })
);
builder.Services.AddHangfireServer(options =>
{
    options.SchedulePollingInterval = TimeSpan.FromMilliseconds(10);
});
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
    .AddService(serviceName: builder.Configuration["OpenTelemetry:ServiceName"], serviceVersion: "1.0.0");
var otlpEndpoint = new Uri(builder.Configuration["OpenTelemetry:Endpoint"]);

var otelBuilder = builder.Services.AddOpenTelemetry();
otelBuilder.WithTracing(tracing =>
{
    tracing
        .SetResourceBuilder(resourceBuilder)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter(o => o.Endpoint = otlpEndpoint);
});
otelBuilder.WithMetrics(metrics =>
{
    metrics
        .SetResourceBuilder(resourceBuilder)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
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
