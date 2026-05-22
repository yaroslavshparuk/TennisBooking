using TennisBooking.Application.Abstractions;

namespace TennisBooking.Application.Booking;

public sealed class ExecuteBookingUseCase
{
    private readonly ISkeddaClient _skeddaClient;
    private readonly INotificationSender _notificationSender;
    private readonly IBookingDeduplicationStore _deduplicationStore;
    private readonly IBookingCancellationLinkRepository _bookingCancellationLinkRepository;

    public ExecuteBookingUseCase(
        ISkeddaClient skeddaClient,
        INotificationSender notificationSender,
        IBookingDeduplicationStore deduplicationStore,
        IBookingCancellationLinkRepository bookingCancellationLinkRepository)
    {
        _skeddaClient = skeddaClient;
        _notificationSender = notificationSender;
        _deduplicationStore = deduplicationStore;
        _bookingCancellationLinkRepository = bookingCancellationLinkRepository;
    }

    public async Task ExecuteAsync(PreparedBooking? booking, CancellationToken cancellationToken)
    {
        if (booking is null)
            return;

        var bookingKey = BuildBookingKey(booking);
        if (!_deduplicationStore.TryBegin(bookingKey))
            return;

        try
        {
            var bookResult = await _skeddaClient.BookAsync(booking, cancellationToken);
            var telegramResult = await _notificationSender.NotifyBookingSucceededAsync(booking.UserConfig, booking.Slot, cancellationToken);
            await _bookingCancellationLinkRepository.SaveAsync(
                booking.UserConfig,
                booking.Slot,
                telegramResult.ChatId,
                telegramResult.MessageId,
                bookResult.BookingId,
                cancellationToken);
        }
        catch
        {
            _deduplicationStore.Release(bookingKey);
            throw;
        }
    }

    private static string BuildBookingKey(PreparedBooking booking)
        => $"{booking.UserConfig.Username}:{booking.UserConfig.ResourceId}:{booking.Slot.StartTime.ToUnixTimeMilliseconds()}";
}
