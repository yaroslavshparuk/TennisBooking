using TennisBooking.Application.Abstractions;

namespace TennisBooking.Application.Booking;

public sealed class ScheduleBookingsUseCase
{
    private readonly IUserBookingConfigRepository _userConfigs;
    private readonly IBookingScheduler _bookingScheduler;

    public ScheduleBookingsUseCase(
        IUserBookingConfigRepository userConfigs,
        IBookingScheduler bookingScheduler)
    {
        _userConfigs = userConfigs;
        _bookingScheduler = bookingScheduler;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var configs = await _userConfigs.GetAllAsync(cancellationToken);
        foreach (var config in configs)
            _bookingScheduler.ScheduleRecurringPreparation(config);
    }
}
