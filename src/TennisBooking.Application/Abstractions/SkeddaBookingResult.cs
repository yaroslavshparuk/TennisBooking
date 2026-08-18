namespace TennisBooking.Application.Abstractions;

/// <summary>
/// A successful Skedda booking. <paramref name="StatusCode"/> carries the HTTP status the POST came
/// back with, so a burst shot can log the status it actually saw rather than an assumed 200 — the
/// burst is tuned from these logs, so every shot must report what really happened.
/// </summary>
public sealed record SkeddaBookingResult(string BookingId, int StatusCode = 200);
