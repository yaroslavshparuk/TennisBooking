using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using TennisBooking.Application.Abstractions;
using TennisBooking.Application.Booking;

namespace TennisBooking.HealthChecks;

public class PreparationHealthCheck : IHealthCheck
{
    private readonly PrepareBookingUseCase _prepareBooking;
    private readonly IUserBookingConfigRepository _userConfigs;
    private readonly ILogger<PreparationHealthCheck> _logger;

    public PreparationHealthCheck(
        PrepareBookingUseCase prepareBooking,
        IUserBookingConfigRepository userConfigs,
        ILogger<PreparationHealthCheck> logger)
    {
        _prepareBooking = prepareBooking;
        _userConfigs = userConfigs;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var userConfig = await _userConfigs.FirstOrDefaultAsync(cancellationToken);
            await _prepareBooking.ExecuteAsync(userConfig, false, cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while checking preparation");
            return HealthCheckResult.Unhealthy();
        }
    }
}
