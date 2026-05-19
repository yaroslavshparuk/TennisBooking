using TennisBooking.Application.Abstractions;

namespace TennisBooking.Application.Booking;

public sealed class ExecuteBookingUseCase
{
    private readonly ISkeddaClient _skeddaClient;
    private readonly INotificationSender _notificationSender;
    private readonly IBookingDeduplicationStore _deduplicationStore;

    public ExecuteBookingUseCase(
        ISkeddaClient skeddaClient,
        INotificationSender notificationSender,
        IBookingDeduplicationStore deduplicationStore)
    {
        _skeddaClient = skeddaClient;
        _notificationSender = notificationSender;
        _deduplicationStore = deduplicationStore;
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
            await _skeddaClient.BookAsync(booking, cancellationToken);
            await _notificationSender.NotifyBookingSucceededAsync(booking.UserConfig, booking.Slot, cancellationToken);
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
