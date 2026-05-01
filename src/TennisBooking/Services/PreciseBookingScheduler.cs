namespace TennisBooking.Services;

using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Models;

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
