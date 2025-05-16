using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TennisBooking.DAL;
using TennisBooking.Services;

namespace TennisBooking.HealthChecks;

public class PreparationHealthCheck : IHealthCheck
{
    private readonly BookingService _bookingService;
    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<PreparationHealthCheck> _logger;
    public PreparationHealthCheck(
        BookingService bookingService,
        ApplicationDbContext dbContext,
        ILogger<PreparationHealthCheck> logger)
    {
        _bookingService = bookingService;
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) {
        try {
            var userConfig = await _dbContext.UserConfigs.FirstOrDefaultAsync();
            await _bookingService.Preparation(userConfig, false, cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception e) {
            _logger.LogError(e, "Error while checking preparation");
            return HealthCheckResult.Unhealthy();
        }
    }
}
