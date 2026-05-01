namespace TennisBooking.Services;

using System.Collections.Concurrent;
using System.Globalization;
using Models;
using Hangfire;
using DAL;
using DAL.Models;
using System.Text.RegularExpressions;
using System.Net;
using Microsoft.EntityFrameworkCore;
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
    private static readonly ConcurrentDictionary<string, byte> BookingKeys = new();

    private readonly ILogger<BookingService> _logger;
    private readonly SkeddaOptions _opts;
    private readonly ApplicationDbContext _db;
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly TelegramService _telegram;
    private readonly IPreciseBookingScheduler _preciseBookingScheduler;

    public BookingService(
        ILogger<BookingService> logger,
        IOptions<SkeddaOptions> opts,
        ApplicationDbContext db,
        TelegramService telegram,
        IBackgroundJobClient backgroundJobClient,
        IPreciseBookingScheduler preciseBookingScheduler) {
        _logger = logger;
        _opts = opts.Value;
        _db = db;
        _backgroundJobClient = backgroundJobClient;
        _telegram = telegram;
        _preciseBookingScheduler = preciseBookingScheduler;
    }

    public async Task Preparation(UserConfig userConfig, bool scheduleBookingJob, CancellationToken ct) {
        if (userConfig == null) {
            _logger.LogError("UserConfig is null");
            return;
        }

        try {
            var startTime = GetBookingStartTime(userConfig);
            var bookingInfo = await PrepareBookingInfo(userConfig, startTime, ct);

            if (scheduleBookingJob) {
                var targetTime = startTime.AddDays(-14);
                _preciseBookingScheduler.ScheduleBooking(bookingInfo);

                // Fallback/recovery path in case the in-process precise timer misses the slot.
                // Only stable identifiers are persisted in Hangfire, not prepared cookies/tokens/passwords.
                var fallbackRunAt = targetTime.AddSeconds(3);
                if (fallbackRunAt < DateTimeOffset.UtcNow)
                    fallbackRunAt = DateTimeOffset.UtcNow.AddSeconds(3);

                _backgroundJobClient.Schedule<BookingService>(
                    x => x.BookingFallback(userConfig.Id, startTime, CancellationToken.None),
                    fallbackRunAt);
            }
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Failed to book court for user {ConfigId}", userConfig.Username);
            throw;
        }
    }

    public async Task BookingFallback(int userConfigId, DateTimeOffset startTime, CancellationToken ct) {
        var userConfig = await _db.UserConfigs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == userConfigId, ct);
        if (userConfig == null)
            throw new InvalidOperationException($"UserConfig {userConfigId} not found.");

        var bookingInfo = await PrepareBookingInfo(userConfig, startTime, ct);
        await Booking(bookingInfo, ct);
    }

    [AutomaticRetry(Attempts = 0)]
    public async Task Booking(BookingInfo bookingInfo, CancellationToken ct) {
        if (bookingInfo == null) {
            _logger.LogError("BookingInfo is null");
            return;
        }

        var bookingKey = BuildBookingKey(bookingInfo);
        if (!BookingKeys.TryAdd(bookingKey, 0)) {
            _logger.LogInformation("Skipping duplicate booking attempt for {BookingKey}", bookingKey);
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
            if (!bookResp.IsSuccessStatusCode) {
                throw new HttpRequestException($"Response from Skedda {await bookResp.Content.ReadAsStringAsync(ct)}");
            }
            bookResp.EnsureSuccessStatusCode();
            _logger.LogInformation("Booked court for user {ConfigId}", bookingInfo.UserConfig.Username);
            var ukrainian = new CultureInfo("uk-UA");
            var dayAndDate = bookingInfo.StartTime.ToString("dddd d MMMM", ukrainian);
            var startTime = bookingInfo.StartTime.ToString("HH:mm", ukrainian);
            var endTime = bookingInfo.StartTime.AddHours(1).ToString("HH:mm", ukrainian);
            var msg = $"🎾 Забронював тенісний корт в Галактиці, {dayAndDate}, {startTime}–{endTime}";
            await _telegram.NotifyAsync(msg);
        }
        catch (Exception ex) {
            BookingKeys.TryRemove(bookingKey, out _);
            _logger.LogError(ex, "Failed to book court for user {ConfigId}", bookingInfo.UserConfig.Username);
            throw;
        }
    }

    private async Task<BookingInfo> PrepareBookingInfo(UserConfig userConfig, DateTimeOffset startTime, CancellationToken ct) {
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

        return new BookingInfo {
            UserConfig = userConfig,
            Body = bookingBody,
            RequestVerificationToken = requestVerificationToken,
            CsrfCookie = csrfCookie,
            ApplicationCookie = applicationCookie,
            StartTime = startTime,
        };
    }

    private static DateTimeOffset GetBookingStartTime(UserConfig userConfig) {
        var bookingDay = DateTimeOffset.UtcNow.AddDays(14);
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");
        return new DateTimeOffset(
            bookingDay.Year,
            bookingDay.Month,
            bookingDay.Day,
            userConfig.Hour,
            0,
            0,
            0,
            tz.GetUtcOffset(bookingDay));
    }

    private static string BuildBookingKey(BookingInfo bookingInfo)
        => $"{bookingInfo.UserConfig.Username}:{bookingInfo.UserConfig.ResourceId}:{bookingInfo.StartTime.ToUnixTimeMilliseconds()}";

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
