namespace TennisBooking.Application.Abstractions;

public interface IWeatherForecastProvider
{
    /// <summary>
    /// Returns the forecast for the hour containing <paramref name="atUtc"/>, or null when no forecast
    /// is available (feature disabled, the instant is outside the forecast horizon, or the upstream
    /// response carried no data for that hour).
    /// </summary>
    Task<WeatherForecast?> GetForecastAsync(DateTimeOffset atUtc, CancellationToken cancellationToken);
}
