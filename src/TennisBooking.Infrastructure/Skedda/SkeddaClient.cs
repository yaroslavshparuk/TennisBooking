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
    private const string AccountLoginPath = "/account/login";
    private const string LoginPath = "/logins";
    private const string GetBookingsPath = "/booking";
    private const string BookingPath = "/bookings";
    private const string CsrfHeaderName = "x-skedda-requestverificationtoken";
    private const string CookieHeaderName = "Cookie";
    private const string ApplicationCookieName = "X-Skedda-ApplicationCookie";
    private const string CsrfCookieName = "X-Skedda-RequestVerificationCookie";

    private readonly SkeddaOptions _options;
    private readonly ILogger<SkeddaClient> _logger;

    public SkeddaClient(IOptions<SkeddaOptions> options, ILogger<SkeddaClient> logger)
    {
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
        return new PreparedBooking(
            userConfig,
            slot,
            BuildBookingBody(userConfig, slot),
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
        var baseUri = new Uri(_options.ApiBaseUrl);
        using var client = new HttpClient { BaseAddress = baseUri };

        var bookReq = new HttpRequestMessage(HttpMethod.Post, BookingPath);
        bookReq.Content = JsonContent(booking.Body);
        bookReq.Headers.Add(CsrfHeaderName, booking.RequestVerificationToken);
        bookReq.Headers.Add(CookieHeaderName,
            $"{CsrfCookieName}={booking.CsrfCookie}; {ApplicationCookieName}={booking.ApplicationCookie}");

        var bookResp = await client.SendAsync(bookReq, cancellationToken);
        if (!bookResp.IsSuccessStatusCode)
            throw new HttpRequestException($"Response from Skedda {await bookResp.Content.ReadAsStringAsync(cancellationToken)}");

        var body = await bookResp.Content.ReadAsStringAsync(cancellationToken);
        dynamic? json = JsonConvert.DeserializeObject(body);
        var bookingId = (string?)json?.booking?.id;
        if (string.IsNullOrWhiteSpace(bookingId))
            throw new InvalidOperationException("Skedda booking id was not found in response.");

        _logger.LogInformation("Skedda booking succeeded with id {SkeddaBookingId}", bookingId);
        return new SkeddaBookingResult(bookingId);
    }

    public async Task CancelAsync(PreparedBooking booking, string bookingId, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Sending Skedda cancel request for booking {SkeddaBookingId}, user {Username}",
            bookingId,
            booking.UserConfig.Username);
        var session = await CreateSessionAsync(booking.UserConfig, cancellationToken);

        var baseUri = new Uri(_options.ApiBaseUrl);
        using var client = new HttpClient { BaseAddress = baseUri };
        var deleteReq = new HttpRequestMessage(HttpMethod.Delete, $"{BookingPath}/{bookingId}");
        deleteReq.Headers.Add(CsrfHeaderName, session.RequestVerificationToken);
        deleteReq.Headers.Add(CookieHeaderName,
            $"{CsrfCookieName}={session.CsrfCookie}; {ApplicationCookieName}={session.ApplicationCookie}");

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
