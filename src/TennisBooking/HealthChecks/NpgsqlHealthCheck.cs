using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace TennisBooking.HealthChecks;

public class NpgsqlHealthCheck : IHealthCheck
{
    private readonly string _connectionString;

    public NpgsqlHealthCheck(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(ex.Message, ex);
        }
        return HealthCheckResult.Healthy();
    }
}
