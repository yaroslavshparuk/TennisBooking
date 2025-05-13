using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TennisBooking.Options;
using TennisBooking.Services;

await Host.CreateDefaultBuilder(args)
    .ConfigureServices((ctx, services) =>
    {
        var skeddaConfig = ctx.Configuration.GetSection("SkeddaConfig");
        services.Configure<SkeddaOptions>(skeddaConfig);
        services.AddHttpClient<BookingService>(client =>
        {
            var opts = skeddaConfig.Get<SkeddaOptions>();
            client.BaseAddress = new Uri(opts.ApiBaseUrl);
        });
        services.AddHostedService<BookingService>();
    })
    .RunConsoleAsync();