using TennisBooking.Domain.Booking;

namespace TennisBooking.Application.Abstractions;

public interface INotificationSender
{
    Task NotifyBookingSucceededAsync(BookingUserConfig userConfig, BookingSlot slot, CancellationToken cancellationToken);
}
