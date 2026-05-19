using TennisBooking.Application.Abstractions;
using TennisBooking.Domain.Booking;

namespace TennisBooking.Application.Booking;

public sealed class BookingFallbackUseCase
{
    private readonly IUserBookingConfigRepository _userConfigs;
    private readonly ISkeddaClient _skeddaClient;
    private readonly ExecuteBookingUseCase _executeBooking;

    public BookingFallbackUseCase(
        IUserBookingConfigRepository userConfigs,
        ISkeddaClient skeddaClient,
        ExecuteBookingUseCase executeBooking)
    {
        _userConfigs = userConfigs;
        _skeddaClient = skeddaClient;
        _executeBooking = executeBooking;
    }

    public async Task ExecuteAsync(int userConfigId, DateTimeOffset startTime, CancellationToken cancellationToken)
    {
        var userConfig = await _userConfigs.GetByIdAsync(userConfigId, cancellationToken);
        if (userConfig is null)
            throw new InvalidOperationException($"UserConfig {userConfigId} not found.");

        var prepared = await _skeddaClient.PrepareBookingAsync(userConfig, new BookingSlot(startTime), cancellationToken);
        await _executeBooking.ExecuteAsync(prepared, cancellationToken);
    }
}
