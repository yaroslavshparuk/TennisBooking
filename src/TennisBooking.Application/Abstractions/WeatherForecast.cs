namespace TennisBooking.Application.Abstractions;

/// <summary>
/// Forecast for the single hour a booking starts in. <see cref="IsRainExpected"/> is decided by the
/// provider (which owns the configured thresholds) rather than by the caller, so the reminder text
/// only has to pick a wording.
/// </summary>
public sealed record WeatherForecast(
    double TemperatureC,
    int PrecipitationProbabilityPercent,
    double PrecipitationMm,
    bool IsRainExpected);
