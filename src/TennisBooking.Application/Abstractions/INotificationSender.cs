using TennisBooking.Domain.Booking;

namespace TennisBooking.Application.Abstractions;

public interface INotificationSender
{
    Task<TelegramNotificationResult> NotifyBookingSucceededAsync(
        BookingUserConfig userConfig,
        BookingSlot slot,
        CancellationToken cancellationToken);

    Task NotifyMessageAsync(string message, CancellationToken cancellationToken, int? replyToMessageId = null);
}
