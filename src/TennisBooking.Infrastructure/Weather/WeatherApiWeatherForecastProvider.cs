using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TennisBooking.Application.Abstractions;
using TennisBooking.Options;

namespace TennisBooking.Infrastructure.Weather;

/// <summary>
/// Reads the hourly forecast for the court's coordinates from WeatherAPI.com's keyed endpoint.
/// Only the fields the reminder needs are parsed (hour time_epoch, temp_c, chance_of_rain,
/// precip_mm); the hour containing the booking start is matched by epoch value.
/// </summary>
public sealed class WeatherApiWeatherForecastProvider : IWeatherForecastProvider
{
    // Free plan covers a 3-day forecast; reminders fire at most ~24h before the slot, so the
    // wanted hour is always inside this window. Deliberately no hour= filter: it selects by the
    // location's local hour-of-day, which could exclude the wanted UTC hour, while matching below
    // is by absolute epoch. The unfiltered 3-day payload is tens of KB on a non-hot path.
    private const int ForecastDays = 3;

    private readonly HttpClient _http;
    private readonly WeatherOptions _options;
    private readonly ILogger<WeatherApiWeatherForecastProvider> _logger;

    public WeatherApiWeatherForecastProvider(
        HttpClient http,
        IOptions<WeatherOptions> options,
        ILogger<WeatherApiWeatherForecastProvider> logger)
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

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _logger.LogWarning("Weather forecast lookup skipped: no WeatherAPI.com API key is configured");
            return null;
        }

        var hourUtc = TruncateToHour(atUtc.ToUniversalTime());
        var url = BuildRequestUrl();

        using var response = await _http.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"WeatherAPI.com forecast failed with {(int)response.StatusCode} ({response.StatusCode}). Body: {errorBody}",
                null,
                response.StatusCode);
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var forecast = ParseForecast(json, hourUtc);
        if (forecast is null)
        {
            _logger.LogWarning("WeatherAPI.com returned no hourly forecast for {HourUtc:o}", hourUtc);
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

    private string BuildRequestUrl()
    {
        var location = string.Create(CultureInfo.InvariantCulture, $"{_options.Latitude},{_options.Longitude}");
        return $"{_options.ApiBaseUrl.TrimEnd('/')}/forecast.json" +
               $"?key={Uri.EscapeDataString(_options.ApiKey)}" +
               $"&q={Uri.EscapeDataString(location)}" +
               $"&days={ForecastDays}&aqi=no&alerts=no";
    }

    private WeatherForecast? ParseForecast(string json, DateTimeOffset hourUtc)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("forecast", out var forecast)
            || !forecast.TryGetProperty("forecastday", out var days)
            || days.ValueKind != JsonValueKind.Array)
            return null;

        var wantedUnixSeconds = hourUtc.ToUnixTimeSeconds();

        foreach (var day in days.EnumerateArray())
        {
            if (!day.TryGetProperty("hour", out var hours) || hours.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var hour in hours.EnumerateArray())
            {
                if (!hour.TryGetProperty("time_epoch", out var epoch)
                    || !epoch.TryGetInt64(out var epochSeconds)
                    || epochSeconds != wantedUnixSeconds)
                    continue;

                // temp_c is the only value the reminder cannot do without; a missing chance of rain
                // or accumulation just means that signal contributes nothing to the rain decision.
                if (ReadDouble(hour, "temp_c") is not { } temperature)
                    return null;

                var probability = (int)Math.Round(ReadRainChance(hour));
                var precipitation = ReadDouble(hour, "precip_mm") ?? 0;
                var isRainExpected = probability >= _options.RainProbabilityThresholdPercent
                                     || precipitation >= _options.RainPrecipitationThresholdMm;

                return new WeatherForecast(temperature, probability, precipitation, isRainExpected);
            }
        }

        return null;
    }

    // A missing chance_of_rain contributes nothing rather than failing the lookup.
    private static double ReadRainChance(JsonElement hour)
        => ReadDouble(hour, "chance_of_rain") ?? 0;

    // Numeric values are normally JSON numbers, but a numeric string is tolerated just in case.
    private static double? ReadDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var parsed))
            return parsed;

        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var textParsed))
            return textParsed;

        return null;
    }

    private static DateTimeOffset TruncateToHour(DateTimeOffset value)
        => new(value.Year, value.Month, value.Day, value.Hour, 0, 0, TimeSpan.Zero);
}
