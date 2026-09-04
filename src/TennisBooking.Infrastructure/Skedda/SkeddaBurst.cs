using System;
using System.Collections.Generic;
using System.Linq;

namespace TennisBooking.Options;

/// <summary>
/// Single source of the booking-burst plan, shared by the HttpClient registration (Program.cs) and the
/// scheduler (PreciseBookingScheduler) so the number of registered connection "pipes" and the shots that
/// use them can never drift apart.
/// </summary>
public static class SkeddaBurst
{
    /// <summary>
    /// Upper bound on the independent connections a single burst opens. A mistyped (or very wide) offsets
    /// array can't then open an unbounded number of sockets; 16 comfortably covers any sane burst.
    /// </summary>
    public const int MaxPipes = 16;

    /// <summary>Burst offsets (ms, relative to the open instant) used when config supplies none.</summary>
    public static readonly int[] DefaultOffsetsMs = { -90, -60, -30, 0 };

    /// <summary>Send-window deadline (ms after open) used when config supplies none.</summary>
    public const int DefaultStopAfterMs = 1500;

    /// <summary>
    /// The shots a burst fires: the configured offsets (or the defaults when none are configured),
    /// de-duplicated and ordered earliest-first.
    /// </summary>
    public static int[] ResolveOffsets(IReadOnlyList<int>? configuredOffsetsMs)
        => (configuredOffsetsMs is { Count: > 0 } ? configuredOffsetsMs : DefaultOffsetsMs)
            .Distinct()
            .OrderBy(ms => ms)
            .ToArray();

    /// <summary>
    /// How many independent pipes the burst needs: one per shot, capped at <see cref="MaxPipes"/>. Each
    /// pipe is a separately-registered HttpClient with its own connection pool, so shot i (sent on pipe
    /// i) never shares a TCP/HTTP-2 connection with another shot. This exists to test whether the rising
    /// per-shot RTT across a burst comes from OUR one shared connection (separate pipes flatten it) or
    /// from Skedda serialising writes server-side (separate pipes change nothing).
    /// </summary>
    public static int PipeCount(IReadOnlyList<int>? configuredOffsetsMs)
        => Math.Clamp(ResolveOffsets(configuredOffsetsMs).Length, 1, MaxPipes);

    /// <summary>
    /// The pipe shot <paramref name="shotIndex"/> fires on. Distinct shots get distinct pipes (the whole
    /// point of the change: no two shots share a socket) until the burst exceeds
    /// <see cref="MaxPipes"/>, past which pipes are reused round-robin.
    /// </summary>
    public static int PipeForShot(int shotIndex, int pipeCount) => shotIndex % pipeCount;
}
