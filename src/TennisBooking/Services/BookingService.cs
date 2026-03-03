namespace TennisBooking.Services;

using System.Globalization;
using Models;
using Hangfire;
using DAL.Models;
using Microsoft.Extensions.Logging;
using Options;
using Microsoft.Extensions.Options;

public class BookingService
{
    private readonly ILogger<BookingService> _logger;
    private readonly SkeddaOptions _opts;
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly TelegramService _telegram;
    private readonly ISkeddaApiClient _skeddaApiClient;

    public BookingService(
        ILogger<BookingService> logger,
        IOptions<SkeddaOptions> opts,
        TelegramService telegram,
        IBackgroundJobClient backgroundJobClient,
        ISkeddaApiClient skeddaApiClient)
    {
        _logger = logger;
        _opts = opts.Value;
        _backgroundJobClient = backgroundJobClient;
        _telegram = telegram;
        _skeddaApiClient = skeddaApiClient;
    }

    public virtual async Task Preparation(UserConfig userConfig, bool scheduleBookingJob, CancellationToken ct)
    {
        if (userConfig == null)
        {
            _logger.LogError("UserConfig is null");
            return;
        }

        var bookingDay = DateTimeOffset.UtcNow.AddDays(14);
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");
        var startTime = new DateTimeOffset(bookingDay.Year, bookingDay.Month, bookingDay.Day,
            userConfig.Hour, 0, 0, 0, tz.GetUtcOffset(bookingDay));

        try
        {
            var session = await _skeddaApiClient.LoginAndGetSessionAsync(
                userConfig.Username, userConfig.Password, ct);

            var bookingBody = new
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
                    end = startTime.AddHours(1).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
                    endOfLastOccurrence = (object?)null,
                    hideAttendees = true,
                    lockInMargin = 1,
                    paymentStatus = 0,
                    piId = (object?)null,
                    price = 0,
                    recurrenceRule = (object?)null,
                    spaces = new[] { userConfig.ResourceId },
                    start = startTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
                    stripPrivateEventDetails = false,
                    syncType = (object?)null,
                    title = (object?)null,
                    type = 1,
                    unrecognizedOrganizer = false,
                    venue = userConfig.Venue,
                    venueuser = userConfig.VenueUser
                }
            };

            var bookingInfo = new BookingInfo
            {
                UserConfig = userConfig,
                Body = bookingBody,
                RequestVerificationToken = session.RequestVerificationToken,
                CsrfCookie = session.CsrfCookie,
                ApplicationCookie = session.ApplicationCookie,
                StartTime = startTime,
            };

            if (scheduleBookingJob)
            {
                _backgroundJobClient.Schedule<BookingService>(
                    x => x.Booking(bookingInfo, ct), startTime.AddDays(-14));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to book court for user {ConfigId}", userConfig.Username);
            throw;
        }
    }

    public virtual async Task Booking(BookingInfo bookingInfo, CancellationToken ct)
    {
        if (bookingInfo == null)
        {
            _logger.LogError("BookingInfo is null");
            return;
        }

        try
        {
            var session = new SkeddaSession
            {
                RequestVerificationToken = bookingInfo.RequestVerificationToken,
                CsrfCookie = bookingInfo.CsrfCookie,
                ApplicationCookie = bookingInfo.ApplicationCookie
            };

            await _skeddaApiClient.BookAsync(session, bookingInfo.Body, ct);

            _logger.LogInformation("Booked court for user {ConfigId}", bookingInfo.UserConfig.Username);
            var ukrainian = new CultureInfo("uk-UA");
            var dayAndDate = bookingInfo.StartTime.ToString("dddd d MMMM", ukrainian);
            var startTime = bookingInfo.StartTime.ToString("HH:mm", ukrainian);
            var endTime = bookingInfo.StartTime.AddHours(1).ToString("HH:mm", ukrainian);
            var msg = $"🎾 Забронював тенісний корт в Галактиці, {dayAndDate}, {startTime}–{endTime}";
            await _telegram.NotifyAsync(msg);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to book court for user {ConfigId}", bookingInfo.UserConfig.Username);
            throw;
        }
    }
}
