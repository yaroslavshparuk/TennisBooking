using TennisBooking.Application.Abstractions;

namespace TennisBooking.Application.Booking;

public sealed class PrepareBookingForConfigUseCase
{
    private readonly IUserBookingConfigRepository _userConfigs;
    private readonly PrepareBookingUseCase _prepareBooking;

    public PrepareBookingForConfigUseCase(
        IUserBookingConfigRepository userConfigs,
        PrepareBookingUseCase prepareBooking)
    {
        _userConfigs = userConfigs;
        _prepareBooking = prepareBooking;
    }

    public async Task ExecuteAsync(int userConfigId, bool scheduleBookingJob, CancellationToken cancellationToken)
    {
        var userConfig = await _userConfigs.GetByIdAsync(userConfigId, cancellationToken);
        if (userConfig is null)
            throw new InvalidOperationException($"UserConfig {userConfigId} not found.");

        await _prepareBooking.ExecuteAsync(userConfig, scheduleBookingJob, cancellationToken);
    }
}
