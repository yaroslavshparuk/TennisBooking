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
            return;

        var offset = await LoadOffsetAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var updates = await GetUpdatesAsync(offset, stoppingToken);
                foreach (var update in updates)
                {
                    offset = Math.Max(offset, update.UpdateId + 1);
                    await ProcessUpdateAsync(update, stoppingToken);
                    await SaveOffsetAsync(update.UpdateId, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
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
        return lastProcessed.HasValue ? lastProcessed.Value + 1 : 0;
    }

    private async Task SaveOffsetAsync(long updateId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var stateRepo = scope.ServiceProvider.GetRequiredService<ITelegramPollingStateRepository>();
        await stateRepo.SaveLastProcessedUpdateIdAsync(updateId, cancellationToken);
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
            return;

        if (!string.Equals(message.Text.Trim(), "/cancel", StringComparison.Ordinal))
            return;

        using var scope = _scopeFactory.CreateScope();
        var links = scope.ServiceProvider.GetRequiredService<IBookingCancellationLinkRepository>();
        var notification = scope.ServiceProvider.GetRequiredService<INotificationSender>();
        var skedda = scope.ServiceProvider.GetRequiredService<ISkeddaClient>();

        var repliedMessageId = message.ReplyToMessage?.MessageId;
        if (!repliedMessageId.HasValue)
        {
            await notification.NotifyMessageAsync("Use /cancel as a reply to the booking confirmation message.", cancellationToken);
            return;
        }

        var link = await links.GetByReplyAsync(message.Chat.Id, repliedMessageId.Value, cancellationToken);
        if (link is null)
        {
            await notification.NotifyMessageAsync("I couldn't find a booking for that message.", cancellationToken);
            return;
        }

        if (link.CancelledAtUtc.HasValue)
        {
            await notification.NotifyMessageAsync("This booking is already cancelled.", cancellationToken);
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
            await notification.NotifyMessageAsync("This booking is already cancelled.", cancellationToken);
            return;
        }

        await notification.NotifyMessageAsync($"Cancelled booking {link.SkeddaBookingId}.", cancellationToken);
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
