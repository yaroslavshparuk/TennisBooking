using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using TennisBooking.DAL;
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

var connString = builder.Configuration.GetConnectionString("Default")
                 ?? throw new InvalidOperationException("Connection string 'Default' not found.");
builder.Services.AddDbContext<ApplicationDbContext>(opt =>
    opt.UseNpgsql(connString));

builder.Services.AddHangfire(config => config
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(connString,
        new PostgreSqlStorageOptions { SchemaName = "hangfire", QueuePollInterval = TimeSpan.FromSeconds(15) })
    .UseFilter(new AutomaticRetryAttribute
    {
        Attempts        = 3,
        DelaysInSeconds = new[] { 2, 5, 10 }
    })
);
builder.Services.AddHangfireServer(options =>
{
    options.SchedulePollingInterval = TimeSpan.FromMilliseconds(100);
});
builder.Services.AddScoped<BookingService>();

builder.Services.AddControllers();

var app = builder.Build();

app.UseRouting();
app.UseHangfireDashboard();
app.UseEndpoints(endpoints => {
    endpoints.MapControllers();
    endpoints.MapHangfireDashboard();
});
using (var scope = app.Services.CreateScope()) {
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var configs = db.UserConfigs.AsNoTracking().ToList();
    foreach (var cfg in configs) {
        RecurringJob.AddOrUpdate<BookingService>(
            cfg.Username,
            x => x.Preparation(cfg, CancellationToken.None),
            Cron.Weekly(cfg.DayOfWeek, cfg.Hour - 1, 59),
            TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv")
        );
    }
}

app.Run();
