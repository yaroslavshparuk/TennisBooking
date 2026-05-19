using TennisBooking.Application.Abstractions;
using TennisBooking.Domain.Booking;

namespace TennisBooking.Application.Booking;

public sealed class PrepareBookingUseCase
{
    private static readonly TimeSpan FallbackDelayAfterTarget = TimeSpan.FromSeconds(3);
    private static readonly TimeZoneInfo KyivTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");

    private readonly ISkeddaClient _skeddaClient;
    private readonly IBookingScheduler _bookingScheduler;
    private readonly IClock _clock;

    public PrepareBookingUseCase(
        ISkeddaClient skeddaClient,
        IBookingScheduler bookingScheduler,
        IClock clock)
    {
        _skeddaClient = skeddaClient;
        _bookingScheduler = bookingScheduler;
        _clock = clock;
    }

    public async Task<PreparedBooking?> ExecuteAsync(
        BookingUserConfig? userConfig,
        bool scheduleBookingJob,
        CancellationToken cancellationToken)
    {
        if (userConfig is null)
            return null;

        var slot = BookingRules.CreateSlotForNextBookableDate(userConfig, _clock.UtcNow, KyivTimeZone);
        var booking = await _skeddaClient.PrepareBookingAsync(userConfig, slot, cancellationToken);

        if (scheduleBookingJob)
        {
            _bookingScheduler.SchedulePreciseBooking(booking);
            var fallbackRunAt = booking.Slot.BookingOpensAt.Add(FallbackDelayAfterTarget);
            if (fallbackRunAt < _clock.UtcNow)
                fallbackRunAt = _clock.UtcNow.Add(FallbackDelayAfterTarget);

            _bookingScheduler.ScheduleFallback(userConfig.Id, booking.Slot.StartTime, fallbackRunAt);
        }

        return booking;
    }
}
