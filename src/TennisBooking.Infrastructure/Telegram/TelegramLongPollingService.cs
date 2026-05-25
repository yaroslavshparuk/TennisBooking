using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using TennisBooking.Application.Abstractions;
using TennisBooking.Application.Booking;
using TennisBooking.Options;

namespace TennisBooking.Infrastructure.Telegram;

public sealed class TelegramLongPollingService : BackgroundService
{
    private const string BaseUrlPrefix = "https://api.telegram.org/bot";
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly NpgsqlDataSource _npgsqlDataSource;
    private readonly TelegramOptions _options;
    private readonly ILogger<TelegramLongPollingService> _logger;
    private NpgsqlConnection? _lockConnection;

    public TelegramLongPollingService(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        NpgsqlDataSource npgsqlDataSource,
        IOptions<TelegramOptions> options,
        ILogger<TelegramLongPollingService> logger)
    {
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _npgsqlDataSource = npgsqlDataSource;
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

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await TryAcquireLeaderLockAsync(stoppingToken))
                    continue;

                await LogWebhookInfoAsync(stoppingToken);

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
                    catch (TelegramPollingConflictException ex)
                    {
                        _logger.LogWarning(ex, "Telegram getUpdates conflict. This usually means active webhook or another poller. Backing off for {DelaySeconds}s", _options.PollingConflictBackoffSeconds);
                        await Task.Delay(TimeSpan.FromSeconds(_options.PollingConflictBackoffSeconds), stoppingToken);
                    }
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
            finally
            {
                await ReleaseLeaderLockAsync();
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await ReleaseLeaderLockAsync();
    }

    private async Task<bool> TryAcquireLeaderLockAsync(CancellationToken cancellationToken)
    {
        if (_lockConnection is not null)
            return true;

        _lockConnection = await _npgsqlDataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = _lockConnection.CreateCommand();
        cmd.CommandText = "SELECT pg_try_advisory_lock(@key)";
        cmd.Parameters.AddWithValue("key", _options.PollingLeaderLockKey);

        var lockAcquired = (bool)(await cmd.ExecuteScalarAsync(cancellationToken) ?? false);
        if (lockAcquired)
        {
            _logger.LogInformation("Acquired Telegram polling leader lock with key {LockKey}", _options.PollingLeaderLockKey);
            return true;
        }

        await _lockConnection.DisposeAsync();
        _lockConnection = null;
        _logger.LogInformation("Telegram polling leader lock is held by another instance. Retrying in {DelaySeconds}s", _options.PollingStandbyRetrySeconds);
        await Task.Delay(TimeSpan.FromSeconds(_options.PollingStandbyRetrySeconds), cancellationToken);
        return false;
    }

    private async Task ReleaseLeaderLockAsync()
    {
        if (_lockConnection is null)
            return;

        try
        {
            await _lockConnection.DisposeAsync();
        }
        finally
        {
            _lockConnection = null;
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

        if (resp.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            var conflictBody = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new TelegramPollingConflictException(
                $"Telegram getUpdates returned 409 Conflict: {conflictBody}");
        }

        if (!resp.IsSuccessStatusCode)
        {
            var errorBody = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Telegram getUpdates failed with {(int)resp.StatusCode} ({resp.StatusCode}). Body: {errorBody}",
                null,
                resp.StatusCode);
        }

        var json = await resp.Content.ReadAsStringAsync(cancellationToken);
        var envelope = JsonSerializer.Deserialize<TelegramUpdatesResponse>(json) ?? new TelegramUpdatesResponse();
        return envelope.Result ?? [];
    }

    private async Task LogWebhookInfoAsync(CancellationToken cancellationToken)
    {
        try
        {
            var http = _httpClientFactory.CreateClient();
            var url = $"{BaseUrlPrefix}{_options.BotToken}/getWebhookInfo";
            var resp = await http.GetAsync(url, cancellationToken);
            if (!resp.IsSuccessStatusCode)
                return;

            var json = await resp.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var result))
                return;

            var webhookUrl = result.TryGetProperty("url", out var urlNode) ? urlNode.GetString() : null;
            if (string.IsNullOrWhiteSpace(webhookUrl))
            {
                _logger.LogInformation("Telegram webhook is not configured for this bot token");
            }
            else
            {
                _logger.LogWarning("Telegram webhook is configured ({WebhookUrl}); it can conflict with long polling", webhookUrl);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to fetch Telegram webhook info");
        }
    }

    private async Task ProcessUpdateAsync(TelegramUpdate update, CancellationToken cancellationToken)
    {
        var message = update.Message;
        if (message?.Chat is null || string.IsNullOrWhiteSpace(message.Text))
        {
            _logger.LogDebug("Ignoring Telegram update {UpdateId}: no chat or text", update.UpdateId);
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var telegramChats = scope.ServiceProvider.GetRequiredService<ITelegramChatRepository>();
        var activeChat = await telegramChats.GetActiveAsync(cancellationToken);
        if (activeChat is null || message.Chat.Id != activeChat.ChatId)
        {
            _logger.LogDebug("Ignoring Telegram update {UpdateId}: unexpected chat", update.UpdateId);
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

        var notification = scope.ServiceProvider.GetRequiredService<INotificationSender>();
        var cancelBooking = scope.ServiceProvider.GetRequiredService<CancelBookingUseCase>();

        var repliedMessageId = message.ReplyToMessage?.MessageId;
        if (!repliedMessageId.HasValue)
        {
            _logger.LogInformation("Rejecting /cancel message {MessageId}: not sent as reply", message.MessageId);
            await notification.NotifyMessageAsync("Щоб відмінити бронювання, надішліть /cancel у відповідь на повідомлення про бронювання.", cancellationToken);
            return;
        }

        var status = await cancelBooking.ExecuteAsync(
            message.Chat.Id,
            repliedMessageId.Value,
            message.MessageId,
            cancellationToken);
        if (status == CancelBookingStatus.NotFound)
        {
            _logger.LogInformation(
                "Rejecting /cancel message {MessageId}: no booking link for replied message {RepliedMessageId}",
                message.MessageId,
                repliedMessageId.Value);
            await notification.NotifyMessageAsync("Не знайшов бронювання для цього повідомлення.", cancellationToken, repliedMessageId.Value);
            return;
        }

        if (status == CancelBookingStatus.AlreadyCancelled)
        {
            _logger.LogInformation(
                "Skipping /cancel message {MessageId}: booking already cancelled",
                message.MessageId);
            await notification.NotifyMessageAsync("Це бронювання вже скасовано.", cancellationToken, repliedMessageId.Value);
            return;
        }

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

    private sealed class TelegramPollingConflictException : Exception
    {
        public TelegramPollingConflictException(string message) : base(message)
        {
        }
    }
}
