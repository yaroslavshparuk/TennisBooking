using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using TennisBooking.Application.Abstractions;
using TennisBooking.Application.Booking;
using TennisBooking.Domain.Booking;
using TennisBooking.Options;

namespace TennisBooking.Infrastructure.Skedda;

public sealed class SkeddaClient : ISkeddaClient
{
    // Name of the pooled HttpClient registered via AddHttpClient in Program.cs.
    // Referenced there so the registration name and the client requested here can't drift.
    public const string SkeddaHttpClientName = "Skedda";

    private const string AccountLoginPath = "/account/login";
    private const string LoginPath = "/logins";
    private const string GetBookingsPath = "/booking";
    private const string BookingPath = "/bookings";
    private const string CsrfHeaderName = "x-skedda-requestverificationtoken";
    private const string CookieHeaderName = "Cookie";
    private const string ApplicationCookieName = "X-Skedda-ApplicationCookie";
    private const string CsrfCookieName = "X-Skedda-RequestVerificationCookie";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SkeddaOptions _options;
    private readonly ILogger<SkeddaClient> _logger;

    public SkeddaClient(
        IHttpClientFactory httpClientFactory,
        IOptions<SkeddaOptions> options,
        ILogger<SkeddaClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<PreparedBooking> PrepareBookingAsync(
        BookingUserConfig userConfig,
        BookingSlot slot,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Preparing Skedda booking session for user {Username}, slot {SlotStart}",
            userConfig.Username,
            slot.StartTime);
        var session = await CreateSessionAsync(userConfig, cancellationToken);
        _logger.LogInformation("Prepared Skedda booking session for user {Username}", userConfig.Username);
        // Pre-serialize the request body and pre-build the Cookie header now (off the hot path),
        // so BookAsync at the target instant only allocates a StringContent and calls SendAsync.
        var bodyJson = JsonConvert.SerializeObject(BuildBookingBody(userConfig, slot));
        var cookieHeader = BuildCookieHeader(session.CsrfCookie, session.ApplicationCookie);
        return new PreparedBooking(
            userConfig,
            slot,
            bodyJson,
            cookieHeader,
            session.RequestVerificationToken,
            session.CsrfCookie,
            session.ApplicationCookie);
    }

    public async Task<SkeddaBookingResult> BookAsync(PreparedBooking booking, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Sending Skedda booking request for user {Username}, slot {SlotStart}",
            booking.UserConfig.Username,
            booking.Slot.StartTime);
        var client = _httpClientFactory.CreateClient(SkeddaHttpClientName);

        using var bookReq = new HttpRequestMessage(HttpMethod.Post, BookingPath)
        {
            Content = new StringContent(booking.BodyJson, Encoding.UTF8, "application/json")
        };
        bookReq.Headers.Add(CsrfHeaderName, booking.RequestVerificationToken);
        bookReq.Headers.Add(CookieHeaderName, booking.CookieHeader);

        var bookResp = await client.SendAsync(bookReq, cancellationToken);
        if (!bookResp.IsSuccessStatusCode)
        {
            var status = (int)bookResp.StatusCode;
            var errorBody = await bookResp.Content.ReadAsStringAsync(cancellationToken);
            // A client-side business rejection (slot already taken, not open yet, validation) is an
            // expected race outcome and comes back as a NON-auth 4xx. Authentication (401/403), rate
            // limiting (429) and server errors (5xx) are real failures the caller must not mistake for
            // a normal lost race — regardless of what the response body happens to contain.
            var expectedRejection = status is >= 400 and < 500
                && status is not 401 and not 403 and not 429;
            if (expectedRejection)
                throw new SkeddaBookingRejectedException(status, errorBody);
            throw new HttpRequestException($"Skedda booking failed with status {status}: {errorBody}");
        }

        var body = await bookResp.Content.ReadAsStringAsync(cancellationToken);
        dynamic? json = JsonConvert.DeserializeObject(body);
        var bookingId = (string?)json?.booking?.id;
        if (string.IsNullOrWhiteSpace(bookingId))
            throw new InvalidOperationException("Skedda booking id was not found in response.");

        _logger.LogInformation("Skedda booking succeeded with id {SkeddaBookingId}", bookingId);
        return new SkeddaBookingResult(bookingId);
    }

    public async Task<SkeddaWarmupResult> WarmupAsync(PreparedBooking booking, CancellationToken cancellationToken)
    {
        // Issue a cheap UNAUTHENTICATED GET on the pooled client during the pre-warm window so the
        // TCP+TLS handshake (and Front Door edge routing) completes ahead of the target instant,
        // letting the booking POST reuse an already-established connection. Warming the socket does
        // not need the session, so we deliberately do NOT replay the CSRF/cookie headers here.
        // ResponseHeadersRead returns as soon as the connection is established and headers arrive.
        // We also read the server Date header to estimate the host<->Skedda clock skew.
        // Best-effort: never throws except on cancellation.
        try
        {
            var client = _httpClientFactory.CreateClient(SkeddaHttpClientName);
            using var warmReq = new HttpRequestMessage(HttpMethod.Get, AccountLoginPath);
            var sentAt = DateTimeOffset.UtcNow;
            using var warmResp = await client.SendAsync(
                warmReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var roundTrip = DateTimeOffset.UtcNow - sentAt;

            TimeSpan? skew = warmResp.Headers.Date is { } serverDate
                ? EstimateClockSkew(serverDate, sentAt, roundTrip)
                : null;

            _logger.LogInformation(
                "Warmed up Skedda connection for user {Username} (status {StatusCode}, rtt {RttMs:F0} ms, skew {SkewMs})",
                booking.UserConfig.Username,
                (int)warmResp.StatusCode,
                roundTrip.TotalMilliseconds,
                skew?.TotalMilliseconds.ToString("F0") ?? "n/a");
            // Any completed response means the connection is warm, even a non-2xx.
            return new SkeddaWarmupResult(Established: true, ClockSkew: skew);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Skedda connection warm-up failed for user {Username}; proceeding without a pre-warmed connection",
                booking.UserConfig.Username);
            return new SkeddaWarmupResult(Established: false, ClockSkew: null);
        }
    }

    /// <summary>
    /// Estimate the server-minus-host clock offset from a response Date header: the server stamped
    /// <paramref name="serverDate"/> roughly half a round-trip after we sent, so we compare it to our
    /// clock at that same midpoint. Coarse (Date is second-resolution). Exposed for testing.
    /// </summary>
    public static TimeSpan EstimateClockSkew(DateTimeOffset serverDate, DateTimeOffset sentAtUtc, TimeSpan roundTrip)
        => serverDate - (sentAtUtc + TimeSpan.FromTicks(roundTrip.Ticks / 2));

    public async Task CancelAsync(PreparedBooking booking, string bookingId, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Sending Skedda cancel request for booking {SkeddaBookingId}, user {Username}",
            bookingId,
            booking.UserConfig.Username);
        var session = await CreateSessionAsync(booking.UserConfig, cancellationToken);

        var client = _httpClientFactory.CreateClient(SkeddaHttpClientName);
        using var deleteReq = new HttpRequestMessage(HttpMethod.Delete, $"{BookingPath}/{bookingId}");
        deleteReq.Headers.Add(CsrfHeaderName, session.RequestVerificationToken);
        deleteReq.Headers.Add(CookieHeaderName,
            BuildCookieHeader(session.CsrfCookie, session.ApplicationCookie));

        var deleteResp = await client.SendAsync(deleteReq, cancellationToken);
        if (!deleteResp.IsSuccessStatusCode)
            throw new HttpRequestException($"Response from Skedda {await deleteResp.Content.ReadAsStringAsync(cancellationToken)}");
        _logger.LogInformation("Skedda cancellation succeeded for booking {SkeddaBookingId}", bookingId);
    }

    private async Task<SessionData> CreateSessionAsync(BookingUserConfig userConfig, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Creating authenticated Skedda session for user {Username}", userConfig.Username);
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        var baseUri = new Uri(_options.ApiBaseUrl);
        using var client = new HttpClient(handler) { BaseAddress = baseUri };

        var openLoginPage = await client.GetAsync(AccountLoginPath, cancellationToken);
        openLoginPage.EnsureSuccessStatusCode();
        var requestVerificationToken = GetRequestVerificationToken(
            await openLoginPage.Content.ReadAsStringAsync(cancellationToken));

        var loginReq = new HttpRequestMessage(HttpMethod.Post, LoginPath);
        loginReq.Headers.Add(CsrfHeaderName, requestVerificationToken);
        loginReq.Headers.Add(CookieHeaderName,
            $"{CsrfCookieName}={handler.CookieContainer.GetCookies(baseUri)[CsrfCookieName]?.Value}");
        loginReq.Content = JsonContent(new
        {
            login = new
            {
                arbitraryerrors = (object?)null,
                username = userConfig.Username,
                password = userConfig.Password,
                rememberMe = false
            }
        });

        var loginResp = await client.SendAsync(loginReq, cancellationToken);
        loginResp.EnsureSuccessStatusCode();

        var csrfCookie = handler.CookieContainer.GetCookies(baseUri)[CsrfCookieName]?.Value
                         ?? throw new InvalidOperationException("Skedda CSRF cookie not found.");
        var applicationCookie = handler.CookieContainer.GetCookies(baseUri)[ApplicationCookieName]?.Value
                                ?? throw new InvalidOperationException("Skedda application cookie not found.");

        var getBookingsReq = new HttpRequestMessage(HttpMethod.Get, GetBookingsPath);
        getBookingsReq.Headers.Add(CsrfHeaderName, requestVerificationToken);
        getBookingsReq.Headers.Add(CookieHeaderName,
            $"{CsrfCookieName}={csrfCookie}; {ApplicationCookieName}={applicationCookie}");
        var getBookingsResp = await client.SendAsync(getBookingsReq, cancellationToken);
        getBookingsResp.EnsureSuccessStatusCode();
        requestVerificationToken = GetRequestVerificationToken(
            await getBookingsResp.Content.ReadAsStringAsync(cancellationToken));
        return new SessionData(requestVerificationToken, csrfCookie, applicationCookie);
    }

    private static StringContent JsonContent(object value)
        => new(JsonConvert.SerializeObject(value), Encoding.UTF8, "application/json");

    private static string BuildCookieHeader(string csrfCookie, string applicationCookie)
        => $"{CsrfCookieName}={csrfCookie}; {ApplicationCookieName}={applicationCookie}";

    private static object BuildBookingBody(BookingUserConfig userConfig, BookingSlot slot)
        => new
        {
            booking = new
            {
                addConference = false,
                allowInviteOthers = false,
                arbitraryerrors = (object?)null,
                attendees = Array.Empty<int>(),
                availabilityStatus = 1,
                chargeTransactionId = (object?)null,
                checkInAudits = (object?)null,
                createdDate = (object?)null,
                customFields = Array.Empty<int>(),
                decoupleBooking = (object?)null,
                decoupleDate = (object?)null,
                end = slot.EndTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
                endOfLastOccurrence = (object?)null,
                hideAttendees = true,
                lockInMargin = 1,
                paymentStatus = 0,
                piId = (object?)null,
                price = 0,
                recurrenceRule = (object?)null,
                spaces = new[] { userConfig.ResourceId },
                start = slot.StartTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
                stripPrivateEventDetails = false,
                syncType = (object?)null,
                title = (object?)null,
                type = 1,
                unrecognizedOrganizer = false,
                venue = userConfig.Venue,
                venueuser = userConfig.VenueUser
            }
        };

    private static string GetRequestVerificationToken(string loginHtml)
    {
        var match = Regex.Match(
            loginHtml,
            "<input\\s+name=\\\"__RequestVerificationToken\\\"\\s+type=\\\"hidden\\\"\\s+value=\\\"([^\\\"]+)\\\"",
            RegexOptions.IgnoreCase);

        if (!match.Success)
            throw new InvalidOperationException("__RequestVerificationToken not found in login page HTML.");

        return match.Groups[1].Value;
    }

    private sealed record SessionData(string RequestVerificationToken, string CsrfCookie, string ApplicationCookie);
}
