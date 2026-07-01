using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TennisBooking.Application.Abstractions;
using TennisBooking.Domain.Booking;
using TennisBooking.Options;

namespace TennisBooking.Infrastructure.Telegram;

public sealed class TelegramNotificationSender : INotificationSender
{
    private const string BaseUrlPrefix = "https://api.telegram.org/bot";

    private readonly HttpClient _http;
    private readonly TelegramOptions _options;
    private readonly ITelegramChatRepository _telegramChats;
    private readonly ILogger<TelegramNotificationSender> _logger;

    public TelegramNotificationSender(
        HttpClient http,
        IOptions<TelegramOptions> options,
        ITelegramChatRepository telegramChats,
        ILogger<TelegramNotificationSender> logger)
    {
        _http = http;
        _options = options.Value;
        _telegramChats = telegramChats;
        _logger = logger;
    }

    public async Task<TelegramNotificationResult> NotifyBookingSucceededAsync(
        BookingUserConfig userConfig,
        BookingSlot slot,
        CancellationToken cancellationToken)
    {
        var ukrainian = new CultureInfo("uk-UA");
        var dayAndDate = slot.StartTime.ToString("dddd d MMMM", ukrainian);
        var startTime = slot.StartTime.ToString("HH:mm", ukrainian);
        var endTime = slot.EndTime.ToString("HH:mm", ukrainian);
        var message =
            $"🎾 Забронював тенісний корт в Галактиці, {dayAndDate}, {startTime}–{endTime}\n" +
            "Якщо плануєш бути на грі, постав 👍 на це повідомлення.\n" +
            "Щоб відмінити бронювання, надішліть /cancel у відповідь на повідомлення про бронювання.";

        try
        {
            return await SendMessageInternalAsync(message, null, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Telegram notification");
            return new TelegramNotificationResult(0, 0);
        }
    }

    public async Task NotifyMessageAsync(string message, CancellationToken cancellationToken, int? replyToMessageId = null)
        => await SendMessageInternalAsync(message, null, cancellationToken, replyToMessageId);

    private async Task<TelegramNotificationResult> SendMessageInternalAsync(
        string message,
        string? parseMode,
        CancellationToken cancellationToken,
        int? replyToMessageId = null)
    {
        var chat = await _telegramChats.GetActiveAsync(cancellationToken);
        if (chat is null)
        {
            _logger.LogWarning("Skipping Telegram message because no active Telegram chat is configured");
            return new TelegramNotificationResult(0, 0);
        }

        var url = $"{BaseUrlPrefix}{_options.BotToken}/sendMessage";
        var payload = new Dictionary<string, object?>
        {
            ["chat_id"] = chat.ChatId,
            ["text"] = message
        };
        if (!string.IsNullOrWhiteSpace(parseMode))
            payload["parse_mode"] = parseMode;
        if (replyToMessageId.HasValue)
            payload["reply_to_message_id"] = replyToMessageId.Value;

        var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        var resp = await _http.PostAsync(url, content, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var errorBody = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Telegram sendMessage failed with {(int)resp.StatusCode} ({resp.StatusCode}). Body: {errorBody}",
                null,
                resp.StatusCode);
        }
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var messageId = doc.RootElement.GetProperty("result").GetProperty("message_id").GetInt32();
        return new TelegramNotificationResult(chat.ChatId, messageId);
    }
}
