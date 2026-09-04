using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TennisBooking.Application.Abstractions;
using TennisBooking.Application.Booking;
using TennisBooking.Options;

namespace TennisBooking.Infrastructure.Scheduling;

public sealed class PreciseBookingScheduler : IPreciseBookingScheduler, IHostedService, IDisposable
{
    // Begin warming this far ahead, so a failed probe has time to retry and the socket is proven warm
    // well before the instant (a cold TCP+TLS to Skedda costs 1-2 s).
    private static readonly TimeSpan WarmupLeadTime = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WarmupRetryDelay = TimeSpan.FromMilliseconds(500);
    // Once warm, re-ping at this cadence to keep the connection alive up to the deadline.
    private static readonly TimeSpan WarmupKeepAliveInterval = TimeSpan.FromSeconds(2);
    // A stalled warm-up must never delay the POST: stop warming this far before the target instant.
    private static readonly TimeSpan WarmupDeadlineMargin = TimeSpan.FromMilliseconds(300);
    // Above this measured (coarse) skew, warn that the host clock likely needs NTP attention.
    private const double GrossClockSkewWarnMs = 2000;

    private readonly ConcurrentDictionary<string, byte> _scheduled = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SkeddaOptions _options;
    private readonly ILogger<PreciseBookingScheduler> _logger;
    private readonly CancellationTokenSource _stoppingCts = new();
    private int _disposeState;

    public PreciseBookingScheduler(
        IServiceScopeFactory scopeFactory,
        IOptions<SkeddaOptions> options,
        ILogger<PreciseBookingScheduler> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
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
            var pipeCount = SkeddaBurst.PipeCount(_options.BookingSendOffsetsMs);
            using var scope = _scopeFactory.CreateScope();
            var skeddaClient = scope.ServiceProvider.GetRequiredService<ISkeddaClient>();

            // Keep a connection proven-warm right up to the instant. The warm-up also measures the
            // host<->Skedda clock skew, but that is LOGGING-ONLY and never shifts the fire time: the
            // Date header is second-resolution, so a correct local clock can still show up to ~1s of
            // apparent skew (quantization) — larger than the tuned offsets, so correcting on it would
            // hurt. Keep the host clock NTP-synced; we only surface a grossly wrong clock to fix at OS level.
            await EnsureWarmConnectionAsync(skeddaClient, booking, targetTime, pipeCount, key, token);

            var executeBooking = scope.ServiceProvider.GetRequiredService<ExecuteBookingUseCase>();
            var booked = await RunBookingBurstAsync(executeBooking, booking, targetTime, pipeCount, token);
            if (booked)
                _logger.LogInformation("Precise burst booked slot for {Username} at {Target}", booking.UserConfig.Username, targetTime);
            else if (token.IsCancellationRequested)
                _logger.LogInformation("Precise burst cancelled during shutdown for {Key}", key);
            else
                _logger.LogWarning("Precise burst exhausted without booking for {Key}", key);
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

    // Warm EVERY pipe from WarmupLeadTime before the instant, until WarmupDeadlineMargin before the
    // target. Each shot fires on its own pipe (a separate connection pool), so a cold pipe at the instant
    // would make that shot pay a 1-2 s handshake it can't afford — this proves all of them warm, and
    // samples the host->server clock skew as a logging-only side effect.
    private async Task EnsureWarmConnectionAsync(
        ISkeddaClient skeddaClient,
        PreparedBooking booking,
        DateTimeOffset targetTime,
        int pipeCount,
        string key,
        CancellationToken token)
    {
        var deadline = targetTime - WarmupDeadlineMargin;
        var warmStart = targetTime - WarmupLeadTime;
        var now = DateTimeOffset.UtcNow;
        if (warmStart > now)
            await Task.Delay(warmStart - now, token);

        // Warm the pipes on INDEPENDENT loops, each with its own cadence. A shared loop would couple them
        // wrongly in both directions: one pipe that keeps failing must not be retried at the slow
        // keep-alive interval just because a sibling succeeded, and a sibling that is already warm must
        // not be re-pinged at the fast retry interval just because another pipe is dead. Independent loops
        // also mean a pipe that hits the deadline mid-probe only loses its own round — the confirmations
        // and skew samples the other pipes already collected still stand.
        var pipes = await Task.WhenAll(Enumerable.Range(0, pipeCount)
            .Select(pipeId => WarmPipeAsync(skeddaClient, booking, pipeId, deadline, token)));

        var skew = pipes.Select(pipe => pipe.ClockSkew).FirstOrDefault(sample => sample is not null);
        var warmed = pipes.Count(pipe => pipe.Established);
        if (warmed < pipeCount)
            _logger.LogWarning(
                "Only {Warmed}/{PipeCount} Skedda pipes confirmed warm before open for {Key}; a shot on a cold pipe may pay a handshake",
                warmed,
                pipeCount,
                key);
        else
            _logger.LogInformation(
                "All {PipeCount} Skedda pipes confirmed warm before open for {Key}",
                pipeCount,
                key);
        if (skew is { } sk)
        {
            _logger.LogInformation(
                "Estimated host->Skedda clock skew {SkewMs:F0} ms for {Key} (Date-header resolution ~1 s; not used to shift fire time)",
                sk.TotalMilliseconds,
                key);
            if (Math.Abs(sk.TotalMilliseconds) >= GrossClockSkewWarnMs)
                _logger.LogWarning(
                    "Host clock appears ~{SkewMs:F0} ms off Skedda's for {Key}; check NTP/chrony — the burst relies on an accurate local clock",
                    sk.TotalMilliseconds,
                    key);
        }
    }

    // One pipe's warm-up loop: probe until this pipe is established, then keep it alive, up to the
    // deadline. Cadence is decided per pipe (fast retry while cold, slow keep-alive once warm) and never
    // from a sibling's outcome. Established/skew accumulate across rounds, so hitting the deadline
    // mid-probe never discards what earlier rounds already proved about THIS pipe.
    private async Task<(bool Established, TimeSpan? ClockSkew)> WarmPipeAsync(
        ISkeddaClient skeddaClient,
        PreparedBooking booking,
        int pipeId,
        DateTimeOffset deadline,
        CancellationToken token)
    {
        var established = false;
        TimeSpan? skew = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                break;

            using (var warmupCts = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                warmupCts.CancelAfter(remaining);
                try
                {
                    var result = await skeddaClient.WarmupAsync(booking, pipeId, warmupCts.Token);
                    if (result.Established)
                    {
                        established = true;
                        if (result.ClockSkew is { } sample)
                            skew = sample;
                    }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    break; // this pipe hit the warm-up deadline; leave time for the burst
                }
            }

            var pause = established ? WarmupKeepAliveInterval : WarmupRetryDelay;
            if (DateTimeOffset.UtcNow + pause >= deadline)
                break;
            await Task.Delay(pause, token);
        }

        return (established, skew);
    }

    // Fire the same booking several times at configured offsets around the open instant so that,
    // despite a jittery connection, at least one request LANDS as the slot opens. Stop as soon as one
    // attempt succeeds; abandon the rest once the slot is realistically gone.
    private async Task<bool> RunBookingBurstAsync(
        ExecuteBookingUseCase executeBooking,
        PreparedBooking booking,
        DateTimeOffset targetTime,
        int pipeCount,
        CancellationToken followUpToken)
    {
        var offsetsMs = SkeddaBurst.ResolveOffsets(_options.BookingSendOffsetsMs);
        var stopAfterMs = _options.BookingSendStopAfterMs > 0 ? _options.BookingSendStopAfterMs : SkeddaBurst.DefaultStopAfterMs;

        using var burstCts = CancellationTokenSource.CreateLinkedTokenSource(followUpToken);
        var hardStopIn = targetTime + TimeSpan.FromMilliseconds(stopAfterMs) - DateTimeOffset.UtcNow;
        if (hardStopIn <= TimeSpan.Zero)
        {
            // Woke up after the whole burst window already elapsed (GC pause, load, clock step). Fire one
            // immediate best-effort attempt rather than giving up with zero POSTs, as the old code did.
            _logger.LogWarning(
                "Burst woke {LateMs:F0} ms after open for user {Username}; firing one immediate attempt",
                -hardStopIn.TotalMilliseconds,
                booking.UserConfig.Username);
            return await executeBooking.TryBookOnceAsync(booking, 0, 0, followUpToken, followUpToken);
        }
        burstCts.CancelAfter(hardStopIn);

        var shots = offsetsMs
            .Select((offsetMs, index) => FireShotAsync(
                executeBooking, booking, targetTime, offsetMs, SkeddaBurst.PipeForShot(index, pipeCount),
                burstCts, followUpToken))
            .ToArray();
        var results = await Task.WhenAll(shots);
        return results.Any(success => success);
    }

    private async Task<bool> FireShotAsync(
        ExecuteBookingUseCase executeBooking,
        PreparedBooking booking,
        DateTimeOffset targetTime,
        int offsetMs,
        int pipeId,
        CancellationTokenSource burstCts,
        CancellationToken followUpToken)
    {
        try
        {
            var wait = targetTime + TimeSpan.FromMilliseconds(offsetMs) - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, burstCts.Token);

            // Send on the burst token (abortable by a sibling win or the hard stop); run the success
            // follow-ups on the outer token; and stop the remaining shots the instant this one claims
            // the slot — before those (slower) follow-ups run.
            return await executeBooking.TryBookOnceAsync(
                booking, offsetMs, pipeId, burstCts.Token, followUpToken, () => burstCts.Cancel());
        }
        catch (OperationCanceledException)
        {
            // Cancelled because a sibling shot already booked the slot, or the hard-stop deadline hit.
            return false;
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
