using System.Collections.Concurrent;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TennisBooking.Application.Abstractions;
using TennisBooking.Application.Booking;

namespace TennisBooking.Infrastructure.Scheduling;

public sealed class PreciseBookingScheduler : IPreciseBookingScheduler, IHostedService, IDisposable
{
    private static readonly TimeSpan WarmupBeforeTarget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PrecisionStep = TimeSpan.FromMilliseconds(20);
    // A stalled warm-up must never delay the POST: abandon it this far before the target instant.
    private static readonly TimeSpan WarmupDeadlineMargin = TimeSpan.FromMilliseconds(300);

    private readonly ConcurrentDictionary<string, byte> _scheduled = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PreciseBookingScheduler> _logger;
    private readonly CancellationTokenSource _stoppingCts = new();
    private int _disposeState;

    public PreciseBookingScheduler(IServiceScopeFactory scopeFactory, ILogger<PreciseBookingScheduler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        TryCancelStoppingToken();
        return Task.CompletedTask;
    }

    public void ScheduleBooking(PreparedBooking booking)
    {
        var key = BuildKey(booking);
        if (!_scheduled.TryAdd(key, 0))
        {
            _logger.LogInformation("Precise booking timer already scheduled for {Key}", key);
            return;
        }

        _ = Task.Run(() => RunPreciseTimerAsync(booking, key, _stoppingCts.Token));
    }

    private async Task RunPreciseTimerAsync(PreparedBooking booking, string key, CancellationToken token)
    {
        try
        {
            var targetTime = booking.Slot.BookingOpensAt.ToUniversalTime();
            var warmupTime = targetTime - WarmupBeforeTarget;
            var now = DateTimeOffset.UtcNow;

            using var scope = _scopeFactory.CreateScope();

            if (warmupTime > now)
            {
                await Task.Delay(warmupTime - now, token);
                // Pre-open the pooled TCP+TLS connection so the POST below reuses a warm socket.
                // Bound it to a deadline before the target so a slow probe can't delay the POST.
                var remainingToDeadline = (targetTime - WarmupDeadlineMargin) - DateTimeOffset.UtcNow;
                if (remainingToDeadline > TimeSpan.Zero)
                {
                    using var warmupCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    warmupCts.CancelAfter(remainingToDeadline);
                    var skeddaClient = scope.ServiceProvider.GetRequiredService<ISkeddaClient>();
                    try
                    {
                        await skeddaClient.WarmupAsync(booking, warmupCts.Token);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        _logger.LogWarning(
                            "Skedda warm-up exceeded its deadline for {Key}; proceeding without a pre-warmed connection",
                            key);
                    }
                }
            }

            while (DateTimeOffset.UtcNow < targetTime)
            {
                var remaining = targetTime - DateTimeOffset.UtcNow;
                var delay = remaining > PrecisionStep ? PrecisionStep : remaining;
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, token);
            }

            var executeBooking = scope.ServiceProvider.GetRequiredService<ExecuteBookingUseCase>();
            await executeBooking.ExecuteAsync(booking, token);
            _logger.LogInformation("Precise timer triggered booking for {Username} at {Target}", booking.UserConfig.Username, targetTime);
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

    private static string BuildKey(PreparedBooking booking)
        => $"{booking.UserConfig.Username}:{booking.Slot.StartTime.ToUnixTimeMilliseconds()}";

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        TryCancelStoppingToken();
        _stoppingCts.Dispose();
    }

    private void TryCancelStoppingToken()
    {
        try
        {
            _stoppingCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            _logger.LogDebug("Precise booking scheduler cancellation token source was already disposed");
        }
    }
}
