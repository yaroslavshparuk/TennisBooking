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
        catch (Exception ex)
        {
            _deduplicationStore.Release(bookingKey);
            _logger.LogError(ex, "Booking execution failed for key {BookingKey}; dedup key released", bookingKey);
            throw;
        }
    }

    private static string BuildBookingKey(PreparedBooking booking)
        => $"{booking.UserConfig.Username}:{booking.UserConfig.ResourceId}:{booking.Slot.StartTime.ToUnixTimeMilliseconds()}";
}
