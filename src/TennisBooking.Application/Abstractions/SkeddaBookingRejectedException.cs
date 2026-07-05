namespace TennisBooking.Application.Abstractions;

/// <summary>
/// Thrown by <see cref="ISkeddaClient.BookAsync"/> when Skedda rejects the booking for an expected,
/// business reason — the slot is already taken, or it is not open yet. This is the normal outcome of
/// losing (or being early in) the race for a contested slot, and is distinct from an infrastructure or
/// authentication failure, which surfaces as a different exception so the caller can log it loudly
/// instead of treating it as a routine lost race.
/// </summary>
public sealed class SkeddaBookingRejectedException : Exception
{
    public int StatusCode { get; }

    public SkeddaBookingRejectedException(int statusCode, string responseBody)
        : base($"Skedda rejected the booking (status {statusCode}): {responseBody}")
        => StatusCode = statusCode;
}
