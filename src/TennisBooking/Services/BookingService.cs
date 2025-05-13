namespace TennisBooking.Services;

using System.Globalization;
using Models;
using Hangfire;
using DAL.Models;
using System.Text.RegularExpressions;
using System.Net;
using Microsoft.Extensions.Logging;
using Options;
using System.Text;
using Newtonsoft.Json;
using Microsoft.Extensions.Options;

public class BookingService {
    private const string AccountLoginPath = "/account/login";
    private const string LoginPath = "/logins";
    private const string GetBookingsPath = "/booking";
    private const string BookingPath = "/bookings";
    private const string CsrfHeaderName = "x-skedda-requestverificationtoken";
    private const string CookieHeaderName = "Cookie";
    private const string ApplicationCookieName = "X-Skedda-ApplicationCookie";
    private const string CsrfCookieName = "X-Skedda-RequestVerificationCookie";
    private readonly ILogger<BookingService> _logger;
    private readonly SkeddaOptions _opts;
    private readonly IBackgroundJobClient _backgroundJobClient;

    public BookingService(
        ILogger<BookingService> logger,
        IOptions<SkeddaOptions> opts,
        IBackgroundJobClient backgroundJobClient) {
        _logger = logger;
        _opts = opts.Value;
        _backgroundJobClient = backgroundJobClient;
    }

    public async Task Preparation(UserConfig userConfig, CancellationToken ct) {
        if (userConfig == null) {
            _logger.LogError("UserConfig is null");
            return;
        }
        var today = DateTimeOffset.Now;
        var startTime = new DateTime(today.Year, today.Month, today.Day, userConfig.Hour, 0, 0);
        if (startTime.DayOfWeek != userConfig.DayOfWeek) {
             _logger.LogError("Today is wrong day of week for booking");
             return;
        }

        try {
            var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
            var baseUri = new Uri(_opts.ApiBaseUrl);
            using var client = new HttpClient(handler) { BaseAddress = baseUri };

            var openLoginPage = await client.GetAsync(AccountLoginPath, ct);
            openLoginPage.EnsureSuccessStatusCode();
            var loginHtml = await openLoginPage.Content.ReadAsStringAsync(ct);
            var requestVerificationToken = GetRequestVerificationToken(loginHtml);
            var loginReq = new HttpRequestMessage(HttpMethod.Post, LoginPath);
            loginReq.Headers.Add(CsrfHeaderName, requestVerificationToken);
            loginReq.Headers.Add(CookieHeaderName,
                $"{CsrfCookieName}={handler.CookieContainer.GetCookies(baseUri)[CsrfCookieName].Value}");

            loginReq.Content = new StringContent(
                JsonConvert.SerializeObject(new {
                    login = new {
                        arbitraryerrors = (object)null,
                        username = userConfig.Username,
                        password = userConfig.Password,
                        rememberMe = false
                    }
                }),
                Encoding.UTF8,
                "application/json"
            );
            var loginResp = await client.SendAsync(loginReq, ct);
            loginResp.EnsureSuccessStatusCode();

            var csrfCookie = handler.CookieContainer.GetCookies(baseUri)[CsrfCookieName].Value;

            var applicationCookie = handler.CookieContainer.GetCookies(baseUri)[ApplicationCookieName].Value;
            var getBookingsReq = new HttpRequestMessage(HttpMethod.Get, GetBookingsPath);
            getBookingsReq.Headers.Add(CsrfHeaderName, requestVerificationToken);
            getBookingsReq.Headers.Add(CookieHeaderName,
                $"{CsrfCookieName}={csrfCookie}; " +
                $"{ApplicationCookieName}={applicationCookie}");
            var getBookingsResp = await client.SendAsync(getBookingsReq, ct);
            getBookingsResp.EnsureSuccessStatusCode();
            requestVerificationToken = GetRequestVerificationToken(await getBookingsResp.Content.ReadAsStringAsync(ct));

            var bookingBody = new {
                booking = new {
                    addConference = false,
                    allowInviteOthers = false,
                    arbitraryerrors = (object)null,
                    attendees = new int[0],
                    availabilityStatus = 1,
                    chargeTransactionId = (object)null,
                    checkInAudits = (object)null,
                    createdDate = (object)null,
                    customFields = new int[0],
                    decoupleBooking = (object)null,
                    decoupleDate = (object)null,
                    end = startTime.AddHours(1).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
                    endOfLastOccurrence = (object)null,
                    hideAttendees = true,
                    lockInMargin = 1,
                    paymentStatus = 0,
                    piId = (object)null,
                    price = 0,
                    recurrenceRule = (object)null,
                    spaces = new[] { userConfig.ResourceId },
                    start = startTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
                    stripPrivateEventDetails = false,
                    syncType = (object)null,
                    title = (object)null,
                    type = 1,
                    unrecognizedOrganizer = false,
                    venue = userConfig.Venue,
                    venueuser = userConfig.VenueUser
                }
            };
            var bookingInfo = new BookingInfo {
                UserConfig = userConfig,
                Body = bookingBody,
                RequestVerificationToken = requestVerificationToken,
                CsrfCookie = csrfCookie,
                ApplicationCookie = applicationCookie,
            };
            _backgroundJobClient.Schedule<BookingService>(x => x.Booking(bookingInfo, ct), startTime.AddDays(-14));
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Failed to book court for user {ConfigId}", userConfig.Username);
            throw;
        }
    }

    public async Task Booking(BookingInfo bookingInfo, CancellationToken ct) {
        if (bookingInfo == null) {
            _logger.LogError("BookingInfo is null");
            return;
        }

        try {
            var baseUri = new Uri(_opts.ApiBaseUrl);
            using var client = new HttpClient { BaseAddress = baseUri };

            var bookReq = new HttpRequestMessage(HttpMethod.Post, BookingPath);
            bookReq.Content = new StringContent(
                JsonConvert.SerializeObject(bookingInfo.Body),
                Encoding.UTF8,
                "application/json"
            );
            bookReq.Headers.Add(CsrfHeaderName, bookingInfo.RequestVerificationToken);
            bookReq.Headers.Add(CookieHeaderName,
                $"{CsrfCookieName}={bookingInfo.CsrfCookie}; " +
                $"{ApplicationCookieName}={bookingInfo.ApplicationCookie}");
            var bookResp = await client.SendAsync(bookReq, ct);
            _logger.LogInformation("Response from Skedda {resp}", await bookResp.Content.ReadAsStringAsync());
            bookResp.EnsureSuccessStatusCode();
            _logger.LogInformation("Booked court for user {ConfigId}", bookingInfo.UserConfig.Username);
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Failed to book court for user {ConfigId}", bookingInfo.UserConfig.Username);
            throw;
        }
    }

    private string GetRequestVerificationToken(string loginHtml) {
        var match = Regex.Match(
            loginHtml,
            "<input\\s+name=\\\"__RequestVerificationToken\\\"\\s+type=\\\"hidden\\\"\\s+value=\\\"([^\\\"]+)\\\"",
            RegexOptions.IgnoreCase
        );
        if (!match.Success)
            throw new InvalidOperationException("__RequestVerificationToken not found in login page HTML.");
        var requestVerificationToken = match.Groups[1].Value;
        return requestVerificationToken;
    }
}
