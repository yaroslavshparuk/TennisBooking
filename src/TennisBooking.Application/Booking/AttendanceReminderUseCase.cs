using Microsoft.Extensions.Logging;
using TennisBooking.Application.Abstractions;

namespace TennisBooking.Application.Booking;

public sealed class AttendanceReminderUseCase
{
    public const string ReminderType24h = "24h";
    public const string ReminderType2h = "2h";

    private readonly IBookingCancellationLinkRepository _links;
    private readonly INotificationSender _notification;
    private readonly ILogger<AttendanceReminderUseCase> _logger;

    public AttendanceReminderUseCase(
        IBookingCancellationLinkRepository links,
        INotificationSender notification,
        ILogger<AttendanceReminderUseCase> logger)
    {
        _links = links;
        _notification = notification;
        _logger = logger;
    }

    public async Task ExecuteAsync(long chatId, int telegramMessageId, string reminderType, CancellationToken cancellationToken)
    {
        if (reminderType is not (ReminderType24h or ReminderType2h))
            throw new ArgumentOutOfRangeException(nameof(reminderType), reminderType, "Unsupported attendance reminder type.");

        var link = await _links.GetByMessageAsync(chatId, telegramMessageId, cancellationToken);
        if (link is null || link.CancelledAtUtc.HasValue)
        {
            _logger.LogInformation(
                "Skipping attendance reminder {ReminderType} for chat {ChatId}, message {MessageId}: booking missing or cancelled",
                reminderType,
                chatId,
                telegramMessageId);
            return;
        }

        var alreadySent = reminderType == ReminderType24h
            ? link.AttendanceReminder24hSentAtUtc.HasValue
            : link.AttendanceReminder2hSentAtUtc.HasValue;
        if (alreadySent)
        {
            _logger.LogInformation(
                "Skipping attendance reminder {ReminderType} for chat {ChatId}, message {MessageId}: already sent",
                reminderType,
                chatId,
                telegramMessageId);
            return;
        }

        var reminderText = reminderType == ReminderType24h
            ? "Нагадування: завтра корт заброньований. Якщо ніхто не планує бути присутнім, скасуйте бронювання командою /cancel у відповідь на повідомлення про бронювання."
            : "Нагадування: гра вже за 2 години. Якщо ніхто не планує бути присутнім, скасуйте бронювання командою /cancel у відповідь на повідомлення про бронювання.";

        await _notification.NotifyMessageAsync(reminderText, cancellationToken, telegramMessageId);

        // Mark as sent only after a successful delivery. If the send throws, the flag stays
        // unset so Hangfire's retry re-sends, rather than permanently suppressing the reminder.
        await _links.TryMarkReminderSentAsync(chatId, telegramMessageId, reminderType, cancellationToken);

        _logger.LogInformation(
            "Attendance reminder {ReminderType} sent for chat {ChatId}, message {MessageId}",
            reminderType,
            chatId,
            telegramMessageId);
    }
}
