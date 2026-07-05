using TennisBooking.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace TennisBooking.Application.Booking;

public sealed class ExecuteBookingUseCase
{
    private readonly ISkeddaClient _skeddaClient;
    private readonly INotificationSender _notificationSender;
    private readonly IBookingDeduplicationStore _deduplicationStore;
    private readonly IBookingCancellationLinkRepository _bookingCancellationLinkRepository;
    private readonly IBookingScheduler _bookingScheduler;
    private readonly ILogger<ExecuteBookingUseCase> _logger;

    public ExecuteBookingUseCase(
        ISkeddaClient skeddaClient,
        INotificationSender notificationSender,
        IBookingDeduplicationStore deduplicationStore,
        IBookingCancellationLinkRepository bookingCancellationLinkRepository,
        IBookingScheduler bookingScheduler,
        ILogger<ExecuteBookingUseCase> logger)
    {
        _skeddaClient = skeddaClient;
        _notificationSender = notificationSender;
        _deduplicationStore = deduplicationStore;
        _bookingCancellationLinkRepository = bookingCancellationLinkRepository;
        _bookingScheduler = bookingScheduler;
        _logger = logger;
    }

    public async Task ExecuteAsync(PreparedBooking? booking, CancellationToken cancellationToken)
    {
        if (booking is null)
        {
            _logger.LogInformation("Execute booking called with null prepared booking");
            return;
        }

        var bookingKey = BuildBookingKey(booking);
        if (!_deduplicationStore.TryBegin(bookingKey))
        {
            _logger.LogInformation("Skipping duplicate booking execution for key {BookingKey}", bookingKey);
            return;
        }

        try
        {
            _logger.LogInformation(
                "Executing booking for user {Username}, resource {ResourceId}, slot {SlotStart}",
                booking.UserConfig.Username,
                booking.UserConfig.ResourceId,
                booking.Slot.StartTime);

            var bookResult = await _skeddaClient.BookAsync(booking, cancellationToken);
            await HandleBookingSucceededAsync(booking, bookResult, cancellationToken);
        }
        catch (Exception ex)
        {
            _deduplicationStore.Release(bookingKey);
            _logger.LogError(ex, "Booking execution failed for key {BookingKey}; dedup key released", bookingKey);
            throw;
        }
    }

    /// <summary>
    /// Sends a single booking POST as part of a burst of concurrent attempts. Returns true if a
    /// booking now exists for this slot (this attempt succeeded, or a sibling attempt already won and
    /// is running the follow-ups). Returns false — without throwing — for the expected rejections a
    /// burst produces (a request that arrived before the slot opened, or lost the race to a conflict),
    /// so the caller can keep trying or stop. The success follow-ups run exactly once, latched on the
    /// deduplication store at success time rather than at attempt time, so sibling shots never block
    /// each other from firing.
    /// </summary>
    /// <summary>
    /// Sends one booking POST as part of a burst. <paramref name="sendToken"/> governs only the POST
    /// (so a sibling win or the burst hard-stop can abort an in-flight send); the success follow-ups run
    /// on <paramref name="followUpToken"/> instead, so they are never cut off by the burst deadline.
    /// <paramref name="onBooked"/> is invoked the instant this shot claims the slot — before the slower
    /// follow-ups — so the remaining shots are stopped immediately. Returns true if a booking now exists
    /// for this slot; false only for an expected/failed attempt. Never throws (except nothing — even a
    /// failed follow-up is swallowed, because the booking already exists on Skedda).
    /// </summary>
    public async Task<bool> TryBookOnceAsync(
        PreparedBooking booking,
        int offsetMs,
        CancellationToken sendToken,
        CancellationToken followUpToken,
        Action? onBooked = null)
    {
        SkeddaBookingResult bookResult;
        try
        {
            bookResult = await _skeddaClient.BookAsync(booking, sendToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SkeddaBookingRejectedException ex)
        {
            // Expected: this shot lost the race or arrived before the slot opened.
            _logger.LogInformation(
                "Burst shot at offset {OffsetMs} ms was rejected (expected) for user {Username}, slot {SlotStart}: {Reason}",
                offsetMs,
                booking.UserConfig.Username,
                booking.Slot.StartTime,
                ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            // Unexpected: auth failure, 5xx, TLS, malformed response — a real regression, not a lost race.
            _logger.LogWarning(
                ex,
                "Burst shot at offset {OffsetMs} ms failed unexpectedly for user {Username}, slot {SlotStart}",
                offsetMs,
                booking.UserConfig.Username,
                booking.Slot.StartTime);
            return false;
        }

        var bookingKey = BuildBookingKey(booking);
        if (!_deduplicationStore.TryBegin(bookingKey))
        {
            // Another shot already claimed the slot. This shot's POST also succeeded, so a second
            // booking may now exist on Skedda that we do not track — surface it loudly.
            _logger.LogWarning(
                "Burst shot at offset {OffsetMs} ms also booked slot (id {SkeddaBookingId}) but another shot already claimed {BookingKey}; this duplicate booking is untracked (no cancellation link/reminders)",
                offsetMs,
                bookResult.BookingId,
                bookingKey);
            return true;
        }

        // Slot is ours. Stop the remaining shots NOW, before the (slower) follow-ups run.
        onBooked?.Invoke();
        _logger.LogInformation(
            "Burst shot at offset {OffsetMs} ms won the booking for user {Username}, slot {SlotStart}",
            offsetMs,
            booking.UserConfig.Username,
            booking.Slot.StartTime);

        try
        {
            await HandleBookingSucceededAsync(booking, bookResult, followUpToken);
        }
        catch (Exception ex)
        {
            // The booking exists on Skedda; a follow-up failure must not lose it, fault the burst, or
            // release the dedup latch (releasing would make the +3s fallback re-POST into a conflict).
            _logger.LogError(
                ex,
                "Booking {SkeddaBookingId} succeeded but follow-ups failed for {BookingKey}; booking exists but notification/cancellation link/reminders may be incomplete",
                bookResult.BookingId,
                bookingKey);
        }

        return true;
    }

    private async Task HandleBookingSucceededAsync(
        PreparedBooking booking,
        SkeddaBookingResult bookResult,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Skedda booking created with id {SkeddaBookingId}", bookResult.BookingId);

        var telegramResult = await _notificationSender.NotifyBookingSucceededAsync(booking.UserConfig, booking.Slot, cancellationToken);
        _logger.LogInformation(
            "Telegram success notification sent: chat {ChatId}, message {MessageId}",
            telegramResult.ChatId,
            telegramResult.MessageId);

        if (telegramResult.ChatId == 0 || telegramResult.MessageId == 0)
        {
            _logger.LogWarning(
                "Skipping Telegram cancellation link and attendance reminders because no Telegram message was sent");
            return;
        }

        await _bookingCancellationLinkRepository.SaveAsync(
            booking.UserConfig,
            booking.Slot,
            telegramResult.ChatId,
            telegramResult.MessageId,
            bookResult.BookingId,
            cancellationToken);
        _logger.LogInformation(
            "Stored cancellation link: chat {ChatId}, message {MessageId}, booking {SkeddaBookingId}",
            telegramResult.ChatId,
            telegramResult.MessageId,
            bookResult.BookingId);

        var reminder24hAt = booking.Slot.StartTime.ToUniversalTime().AddHours(-24);
        var reminder2hAt = booking.Slot.StartTime.ToUniversalTime().AddHours(-2);
        var reminder24hJobId = _bookingScheduler.ScheduleAttendanceCheck(
            telegramResult.ChatId,
            telegramResult.MessageId,
            booking.Slot.StartTime.ToUniversalTime(),
            AttendanceReminderUseCase.ReminderType24h,
            reminder24hAt);
        var activeAfter24hSchedule = await _bookingCancellationLinkRepository.SaveReminderJobIdAsync(
            telegramResult.ChatId,
            telegramResult.MessageId,
            AttendanceReminderUseCase.ReminderType24h,
            reminder24hJobId,
            cancellationToken);
        if (!activeAfter24hSchedule)
            _bookingScheduler.DeleteAttendanceCheck(reminder24hJobId);

        var reminder2hJobId = _bookingScheduler.ScheduleAttendanceCheck(
            telegramResult.ChatId,
            telegramResult.MessageId,
            booking.Slot.StartTime.ToUniversalTime(),
            AttendanceReminderUseCase.ReminderType2h,
            reminder2hAt);
        var activeAfter2hSchedule = await _bookingCancellationLinkRepository.SaveReminderJobIdAsync(
            telegramResult.ChatId,
            telegramResult.MessageId,
            AttendanceReminderUseCase.ReminderType2h,
            reminder2hJobId,
            cancellationToken);
        if (!activeAfter2hSchedule)
            _bookingScheduler.DeleteAttendanceCheck(reminder2hJobId);
    }

    private static string BuildBookingKey(PreparedBooking booking)
        => $"{booking.UserConfig.Username}:{booking.UserConfig.ResourceId}:{booking.Slot.StartTime.ToUnixTimeMilliseconds()}";
}
