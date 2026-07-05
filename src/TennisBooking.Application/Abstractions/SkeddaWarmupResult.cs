namespace TennisBooking.Application.Abstractions;

/// <summary>
/// Outcome of a single pre-open warm-up probe.
/// </summary>
/// <param name="Established">True if the probe completed a request (so the pooled TCP+TLS connection is warm).</param>
/// <param name="ClockSkew">
/// Estimated offset between the Skedda server clock and this host's clock (server minus host), derived
/// from the response <c>Date</c> header adjusted for round-trip, or null if the header was absent. The
/// Date header is second-resolution, so treat this as coarse.
/// </param>
public sealed record SkeddaWarmupResult(bool Established, TimeSpan? ClockSkew);
