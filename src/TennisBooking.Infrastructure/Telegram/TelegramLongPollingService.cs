using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TennisBooking.Application.Abstractions;
using TennisBooking.Application.Booking;
using TennisBooking.Options;

namespace TennisBooking.Infrastructure.Telegram;

public sealed class TelegramLongPollingService : BackgroundService
{
    private const string BaseUrlPrefix = "https://api.telegram.org/bot";
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TelegramOptions _options;
    private readonly ILogger<TelegramLongPollingService> _logger;

    public TelegramLongPollingService(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        IOptions<TelegramOptions> options,
        ILogger<TelegramLongPollingService> logger)
    {
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_options.BotToken))
        {
            _logger.LogWarning("Telegram long polling is disabled because BotToken is empty");
            return;
        }

        var offset = await LoadOffsetAsync(stoppingToken);
        _logger.LogInformation("Telegram long polling started with offset {Offset}", offset);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var updates = await GetUpdatesAsync(offset, stoppingToken);
                if (updates.Count > 0)
                    _logger.LogInformation("Received {Count} Telegram updates starting from offset {Offset}", updates.Count, offset);
                foreach (var update in updates)
                {
                    offset = Math.Max(offset, update.UpdateId + 1);
                    await ProcessUpdateAsync(update, stoppingToken);
                    await SaveOffsetAsync(update.UpdateId, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Telegram long polling stopped by cancellation");
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Telegram polling iteration failed");
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }
    }

    private async Task<long> LoadOffsetAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var stateRepo = scope.ServiceProvider.GetRequiredService<ITelegramPollingStateRepository>();
        var lastProcessed = await stateRepo.GetLastProcessedUpdateIdAsync(cancellationToken);
        var offset = lastProcessed.HasValue ? lastProcessed.Value + 1 : 0;
        _logger.LogInformation("Loaded Telegram polling offset {Offset}", offset);
        return offset;
    }

    private async Task SaveOffsetAsync(long updateId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var stateRepo = scope.ServiceProvider.GetRequiredService<ITelegramPollingStateRepository>();
        await stateRepo.SaveLastProcessedUpdateIdAsync(updateId, cancellationToken);
        _logger.LogDebug("Saved Telegram polling offset for update {UpdateId}", updateId);
    }

    private async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, CancellationToken cancellationToken)
    {
        var http = _httpClientFactory.CreateClient();
        var url = $"{BaseUrlPrefix}{_options.BotToken}/getUpdates";
        var payload = new { offset, timeout = 40, allowed_updates = new[] { "message" } };
        var req = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var resp = await http.PostAsync(url, req, cancellationToken);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);
        var envelope = JsonSerializer.Deserialize<TelegramUpdatesResponse>(json) ?? new TelegramUpdatesResponse();
        return envelope.Result ?? [];
    }

    private async Task ProcessUpdateAsync(TelegramUpdate update, CancellationToken cancellationToken)
    {
        var message = update.Message;
        if (message?.Chat?.Id != _options.ChatId || string.IsNullOrWhiteSpace(message.Text))
        {
            _logger.LogDebug("Ignoring Telegram update {UpdateId}: no text or unexpected chat", update.UpdateId);
            return;
        }

        if (!string.Equals(message.Text.Trim(), "/cancel", StringComparison.Ordinal))
        {
            _logger.LogDebug("Ignoring Telegram message {MessageId}: unsupported command text '{Text}'", message.MessageId, message.Text);
            return;
        }

        _logger.LogInformation(
            "Processing /cancel command from chat {ChatId}, message {MessageId}, replyTo {ReplyToMessageId}",
            message.Chat.Id,
            message.MessageId,
            message.ReplyToMessage?.MessageId);

        using var scope = _scopeFactory.CreateScope();
        var links = scope.ServiceProvider.GetRequiredService<IBookingCancellationLinkRepository>();
        var notification = scope.ServiceProvider.GetRequiredService<INotificationSender>();
        var skedda = scope.ServiceProvider.GetRequiredService<ISkeddaClient>();

        var repliedMessageId = message.ReplyToMessage?.MessageId;
        if (!repliedMessageId.HasValue)
        {
            _logger.LogInformation("Rejecting /cancel message {MessageId}: not sent as reply", message.MessageId);
            await notification.NotifyMessageAsync("Використай /cancel як відповідь на повідомлення про бронювання.", cancellationToken);
            return;
        }

        var link = await links.GetByReplyAsync(message.Chat.Id, repliedMessageId.Value, cancellationToken);
        if (link is null)
        {
            _logger.LogInformation(
                "Rejecting /cancel message {MessageId}: no booking link for replied message {RepliedMessageId}",
                message.MessageId,
                repliedMessageId.Value);
            await notification.NotifyMessageAsync("Не знайшов бронювання для цього повідомлення.", cancellationToken, repliedMessageId.Value);
            return;
        }

        if (link.CancelledAtUtc.HasValue)
        {
            _logger.LogInformation(
                "Skipping /cancel for booking {SkeddaBookingId}: already cancelled",
                link.SkeddaBookingId);
            await notification.NotifyMessageAsync("Це бронювання вже скасовано.", cancellationToken, repliedMessageId.Value);
            return;
        }

        var prepared = new PreparedBooking(
            link.UserConfig,
            link.Slot,
            new { },
            string.Empty,
            string.Empty,
            string.Empty);
        await skedda.CancelAsync(prepared, link.SkeddaBookingId, cancellationToken);
        var marked = await links.TryMarkCancelledAsync(message.Chat.Id, repliedMessageId.Value, message.MessageId, cancellationToken);
        if (!marked)
        {
            _logger.LogInformation(
                "Cancellation race detected for booking {SkeddaBookingId}: already cancelled by another request",
                link.SkeddaBookingId);
            await notification.NotifyMessageAsync("Це бронювання вже скасовано.", cancellationToken, repliedMessageId.Value);
            return;
        }

        _logger.LogInformation("Cancellation completed for booking {SkeddaBookingId}", link.SkeddaBookingId);
        await notification.NotifyMessageAsync("Бронювання скасовано.", cancellationToken, repliedMessageId.Value);
    }

    private sealed class TelegramUpdatesResponse
    {
        [JsonPropertyName("result")]
        public List<TelegramUpdate>? Result { get; set; }
    }

    private sealed class TelegramUpdate
    {
        [JsonPropertyName("update_id")]
        public long UpdateId { get; set; }
        [JsonPropertyName("message")]
        public TelegramMessage? Message { get; set; }
    }

    private sealed class TelegramMessage
    {
        [JsonPropertyName("message_id")]
        public int MessageId { get; set; }
        [JsonPropertyName("chat")]
        public TelegramChat? Chat { get; set; }
        [JsonPropertyName("text")]
        public string? Text { get; set; }
        [JsonPropertyName("reply_to_message")]
        public TelegramReplyMessage? ReplyToMessage { get; set; }
    }

    private sealed class TelegramReplyMessage
    {
        [JsonPropertyName("message_id")]
        public int MessageId { get; set; }
    }

    private sealed class TelegramChat
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }
    }
}
