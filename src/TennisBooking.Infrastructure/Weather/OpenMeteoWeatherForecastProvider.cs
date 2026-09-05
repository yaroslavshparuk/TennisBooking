using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TennisBooking.Application.Abstractions;
using TennisBooking.Options;

namespace TennisBooking.Infrastructure.Weather;

/// <summary>
/// Reads the hourly forecast for the court's coordinates from Open-Meteo's free (key-less) endpoint.
/// Only the single hour a booking starts in is requested, via start_hour/end_hour, so the response
/// stays a handful of bytes.
/// </summary>
public sealed class OpenMeteoWeatherForecastProvider : IWeatherForecastProvider
{
    private readonly HttpClient _http;
    private readonly WeatherOptions _options;
    private readonly ILogger<OpenMeteoWeatherForecastProvider> _logger;

    public OpenMeteoWeatherForecastProvider(
        HttpClient http,
        IOptions<WeatherOptions> options,
        ILogger<OpenMeteoWeatherForecastProvider> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<WeatherForecast?> GetForecastAsync(DateTimeOffset atUtc, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Weather forecast lookup skipped: the feature is disabled");
            return null;
        }

        var hourUtc = TruncateToHour(atUtc.ToUniversalTime());
        var url = BuildRequestUrl(hourUtc);

        using var response = await _http.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Open-Meteo forecast failed with {(int)response.StatusCode} ({response.StatusCode}). Body: {errorBody}",
                null,
                response.StatusCode);
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var forecast = ParseForecast(json, hourUtc);
        if (forecast is null)
        {
            _logger.LogWarning("Open-Meteo returned no hourly forecast for {HourUtc:o}", hourUtc);
            return null;
        }

        _logger.LogInformation(
            "Weather forecast for {HourUtc:o}: {TemperatureC} °C, precipitation {PrecipitationMm} mm at {PrecipitationProbability}% (rain expected: {IsRainExpected})",
            hourUtc,
            forecast.TemperatureC,
            forecast.PrecipitationMm,
            forecast.PrecipitationProbabilityPercent,
            forecast.IsRainExpected);

        return forecast;
    }

    private string BuildRequestUrl(DateTimeOffset hourUtc)
    {
        // timeformat=unixtime keeps the returned instants unambiguous, so the hour we asked for is
        // matched by value rather than by trusting the array's ordering. start_hour/end_hour are
        // expressed in the requested timezone, hence timezone=UTC together with a UTC hour.
        var start = hourUtc.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
        var end = hourUtc.AddHours(1).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);

        return $"{_options.ApiBaseUrl.TrimEnd('/')}/v1/forecast" +
               $"?latitude={_options.Latitude.ToString(CultureInfo.InvariantCulture)}" +
               $"&longitude={_options.Longitude.ToString(CultureInfo.InvariantCulture)}" +
               "&hourly=temperature_2m,precipitation_probability,precipitation" +
               "&timezone=UTC&timeformat=unixtime" +
               $"&start_hour={Uri.EscapeDataString(start)}&end_hour={Uri.EscapeDataString(end)}";
    }

    private WeatherForecast? ParseForecast(string json, DateTimeOffset hourUtc)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("hourly", out var hourly)
            || !hourly.TryGetProperty("time", out var times)
            || times.ValueKind != JsonValueKind.Array)
            return null;

        var wantedUnixSeconds = hourUtc.ToUnixTimeSeconds();
        var index = -1;
        for (var i = 0; i < times.GetArrayLength(); i++)
        {
            if (times[i].TryGetInt64(out var value) && value == wantedUnixSeconds)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
            return null;

        // temperature_2m is the only value the reminder cannot do without; a missing probability or
        // accumulation just means that signal contributes nothing to the rain decision.
        if (ReadDouble(hourly, "temperature_2m", index) is not { } temperature)
            return null;

        var probability = (int)Math.Round(ReadDouble(hourly, "precipitation_probability", index) ?? 0);
        var precipitation = ReadDouble(hourly, "precipitation", index) ?? 0;
        var isRainExpected = probability >= _options.RainProbabilityThresholdPercent
                             || precipitation >= _options.RainPrecipitationThresholdMm;

        return new WeatherForecast(temperature, probability, precipitation, isRainExpected);
    }

    private static double? ReadDouble(JsonElement hourly, string propertyName, int index)
    {
        if (!hourly.TryGetProperty(propertyName, out var values)
            || values.ValueKind != JsonValueKind.Array
            || index >= values.GetArrayLength())
            return null;

        var value = values[index];
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var parsed) ? parsed : null;
    }

    private static DateTimeOffset TruncateToHour(DateTimeOffset value)
        => new(value.Year, value.Month, value.Day, value.Hour, 0, 0, TimeSpan.Zero);
}
