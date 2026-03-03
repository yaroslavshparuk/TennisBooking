namespace TennisBooking.Services;

public interface ISkeddaApiClient
{
    /// <summary>
    /// Logs into Skedda, navigates to bookings page, and returns session data needed for booking.
    /// </summary>
    Task<SkeddaSession> LoginAndGetSessionAsync(string username, string password, CancellationToken ct);

    /// <summary>
    /// Submits a booking request using the provided session and body.
    /// </summary>
    Task BookAsync(SkeddaSession session, object body, CancellationToken ct);
}

public class SkeddaSession
{
    public string RequestVerificationToken { get; set; } = string.Empty;
    public string CsrfCookie { get; set; } = string.Empty;
    public string ApplicationCookie { get; set; } = string.Empty;
}
