using TennisBooking.Domain.Booking;

namespace TennisBooking.Application.Abstractions;

public interface IBookingCancellationLinkRepository
{
    Task SaveAsync(
        BookingUserConfig userConfig,
        BookingSlot slot,
        long chatId,
        int telegramMessageId,
        string skeddaBookingId,
        CancellationToken cancellationToken);

    Task<BookingCancellationLink?> GetByReplyAsync(long chatId, int repliedTelegramMessageId, CancellationToken cancellationToken);

    Task<bool> TryMarkCancelledAsync(long chatId, int repliedTelegramMessageId, int cancelRequestMessageId, CancellationToken cancellationToken);
}
