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
    private readonly ILogger<TelegramNotificationSender> _logger;

    public TelegramNotificationSender(
        HttpClient http,
        IOptions<TelegramOptions> options,
        ILogger<TelegramNotificationSender> logger)
    {
        _http = http;
        _options = options.Value;
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
            return new TelegramNotificationResult(_options.ChatId, 0);
        }
    }

    public async Task NotifyMessageAsync(string message, CancellationToken cancellationToken, int? replyToMessageId = null)
        => await SendMessageInternalAsync(message, null, cancellationToken, replyToMessageId);

    public async Task<int> GetThumbsUpReactionCountAsync(long chatId, int messageId, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrlPrefix}{_options.BotToken}/getMessageReactions";
        var payload = new Dictionary<string, object?>
        {
            ["chat_id"] = chatId,
            ["message_id"] = messageId
        };
        var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        var resp = await _http.PostAsync(url, content, cancellationToken);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
            return 0;

        var count = 0;
        foreach (var reaction in result.EnumerateArray())
        {
            if (!reaction.TryGetProperty("type", out var typeNode) || typeNode.GetString() != "emoji")
                continue;
            if (!reaction.TryGetProperty("emoji", out var emojiNode) || emojiNode.GetString() != "👍")
                continue;
            if (!reaction.TryGetProperty("total_count", out var countNode))
                continue;

            count = countNode.GetInt32();
            break;
        }

        return count;
    }

    private async Task<TelegramNotificationResult> SendMessageInternalAsync(
        string message,
        string? parseMode,
        CancellationToken cancellationToken,
        int? replyToMessageId = null)
    {
        var url = $"{BaseUrlPrefix}{_options.BotToken}/sendMessage";
        var payload = new Dictionary<string, object?>
        {
            ["chat_id"] = _options.ChatId,
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
        return new TelegramNotificationResult(_options.ChatId, messageId);
    }
}
