using System.Globalization;
using Microsoft.Extensions.Logging;
using TennisBooking.Application.Abstractions;

namespace TennisBooking.Application.Booking;

public sealed class AttendanceReminderUseCase
{
    public const string ReminderType24h = "24h";
    public const string ReminderType2h = "2h";

    private readonly IBookingCancellationLinkRepository _links;
    private readonly INotificationSender _notification;
    private readonly IWeatherForecastProvider _weather;
    private readonly ILogger<AttendanceReminderUseCase> _logger;

    public AttendanceReminderUseCase(
        IBookingCancellationLinkRepository links,
        INotificationSender notification,
        IWeatherForecastProvider weather,
        ILogger<AttendanceReminderUseCase> logger)
    {
        _links = links;
        _notification = notification;
        _weather = weather;
        _logger = logger;
    }

    public async Task ExecuteAsync(long chatId, int telegramMessageId, string reminderType, CancellationToken cancellationToken)
    {
        if (reminderType is not (ReminderType24h or ReminderType2h))
            throw new ArgumentOutOfRangeException(nameof(reminderType), reminderType, "Unsupported attendance reminder type.");

        var link = await _links.GetByMessageAsync(chatId, telegramMessageId, cancellationToken);
        if (link is null || link.CancelledAtUtc.HasValue)
        {
            _logger.LogInformation(
                "Skipping attendance reminder {ReminderType} for chat {ChatId}, message {MessageId}: booking missing or cancelled",
                reminderType,
                chatId,
                telegramMessageId);
            return;
        }

        var alreadySent = reminderType == ReminderType24h
            ? link.AttendanceReminder24hSentAtUtc.HasValue
            : link.AttendanceReminder2hSentAtUtc.HasValue;
        if (alreadySent)
        {
            _logger.LogInformation(
                "Skipping attendance reminder {ReminderType} for chat {ChatId}, message {MessageId}: already sent",
                reminderType,
                chatId,
                telegramMessageId);
            return;
        }

        var reminderText = reminderType == ReminderType24h
            ? "Нагадування: завтра корт заброньований. Якщо ніхто не планує бути присутнім, скасуйте бронювання командою /cancel у відповідь на повідомлення про бронювання."
            : "Нагадування: гра вже за 2 години. Якщо ніхто не планує бути присутнім, скасуйте бронювання командою /cancel у відповідь на повідомлення про бронювання.";

        var forecastLine = await TryBuildForecastLineAsync(link.Slot.StartTime, reminderType, chatId, telegramMessageId, cancellationToken);
        if (forecastLine is not null)
            reminderText = $"{reminderText}\n{forecastLine}";

        await _notification.NotifyMessageAsync(reminderText, cancellationToken, telegramMessageId);

        // Mark as sent only after a successful delivery. If the send throws, the flag stays
        // unset so Hangfire's retry re-sends, rather than permanently suppressing the reminder.
        await _links.TryMarkReminderSentAsync(chatId, telegramMessageId, reminderType, cancellationToken);

        _logger.LogInformation(
            "Attendance reminder {ReminderType} sent for chat {ChatId}, message {MessageId}",
            reminderType,
            chatId,
            telegramMessageId);
    }

    /// <summary>
    /// The forecast is a nice-to-have on top of the reminder, never a precondition for it: an upstream
    /// outage must not swallow the reminder, nor fail the job into a Hangfire retry that would then be
    /// suppressed by the already-sent flag. So every weather failure degrades to "no forecast line".
    /// </summary>
    private async Task<string?> TryBuildForecastLineAsync(
        DateTimeOffset slotStart,
        string reminderType,
        long chatId,
        int telegramMessageId,
        CancellationToken cancellationToken)
    {
        try
        {
            var forecast = await _weather.GetForecastAsync(slotStart, cancellationToken);
            return forecast is null ? null : FormatForecast(forecast);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Weather forecast unavailable for attendance reminder {ReminderType} for chat {ChatId}, message {MessageId}; sending the reminder without it",
                reminderType,
                chatId,
                telegramMessageId);
            return null;
        }
    }

    private static string FormatForecast(WeatherForecast forecast)
    {
        // Whole degrees with an explicit sign: "+18°C" / "-3°C" reads at a glance, and the extra
        // precision of the raw value means nothing two hours (let alone a day) out.
        var temperature = ((int)Math.Round(forecast.TemperatureC, MidpointRounding.AwayFromZero))
            .ToString("+0;-0;0", CultureInfo.InvariantCulture);

        return forecast.IsRainExpected
            ? $"🌧 Погода на час гри: очікується дощ (ймовірність {forecast.PrecipitationProbabilityPercent}%), близько {temperature}°C."
            : $"☀️ Погода на час гри: дощу не очікується, близько {temperature}°C.";
    }
}
