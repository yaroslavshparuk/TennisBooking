namespace TennisBooking.Options;

public class WeatherOptions
{
    /// <summary>
    /// Turns the forecast line in the attendance reminders on or off. When false the provider returns
    /// no forecast and the reminders keep their original wording.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>WeatherAPI.com base URL.</summary>
    public string ApiBaseUrl { get; set; } = "https://api.weatherapi.com/v1";

    /// <summary>
    /// WeatherAPI.com key (register at https://www.weatherapi.com/signup.aspx; the free plan needs no
    /// card). Also settable via the Weather__ApiKey environment variable. Empty means the provider
    /// degrades to "no forecast line" rather than failing reminders.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Court coordinates. Defaults to Kyiv, where the Galaktyka venue is.</summary>
    public double Latitude { get; set; } = 50.4501;

    public double Longitude { get; set; } = 30.5234;

    /// <summary>
    /// The forecast counts as "rain" when the hourly probability reaches this percentage, or when the
    /// forecast accumulation reaches <see cref="RainPrecipitationThresholdMm"/> — either signal alone is
    /// enough, because a confident forecast of a light shower can still report a modest probability.
    /// </summary>
    public int RainProbabilityThresholdPercent { get; set; } = 40;

    public double RainPrecipitationThresholdMm { get; set; } = 0.2;

    /// <summary>
    /// Upstream call budget. Kept short: a reminder that is a little vaguer beats a reminder that is
    /// late, so a slow forecast is dropped rather than waited on.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 10;
}
