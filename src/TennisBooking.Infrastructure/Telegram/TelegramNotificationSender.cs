using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TennisBooking.Application.Abstractions;
using TennisBooking.DAL;
using TennisBooking.DAL.Models;
using TennisBooking.Domain.Booking;

namespace TennisBooking.Infrastructure.Telegram;

public sealed class TelegramNotificationSender : INotificationSender
{
    private const string BaseUrlPrefix = "https://api.telegram.org/bot";

    private readonly HttpClient _http;
    private readonly ApplicationDbContext _db;
    private readonly ILogger<TelegramNotificationSender> _logger;

    public TelegramNotificationSender(
        HttpClient http,
        ApplicationDbContext db,
        ILogger<TelegramNotificationSender> logger)
    {
        _http = http;
        _db = db;
        _logger = logger;
    }

    public async Task NotifyBookingSucceededAsync(
        BookingUserConfig userConfig,
        BookingSlot slot,
        CancellationToken cancellationToken)
    {
        var ukrainian = new CultureInfo("uk-UA");
        var dayAndDate = slot.StartTime.ToString("dddd d MMMM", ukrainian);
        var startTime = slot.StartTime.ToString("HH:mm", ukrainian);
        var endTime = slot.EndTime.ToString("HH:mm", ukrainian);
        var message = $"🎾 Забронював тенісний корт в Галактиці, {dayAndDate}, {startTime}–{endTime}";

        try
        {
            var cfg = await GetConfigAsync(cancellationToken);
            var url = $"{BaseUrlPrefix}{cfg.BotToken}/sendMessage";
            var payload = new { chat_id = cfg.ChatId, text = message, parse_mode = "MarkdownV2" };
            var content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");

            var resp = await _http.PostAsync(url, content, cancellationToken);
            resp.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Telegram notification");
        }
    }

    private async Task<TelegramConfig> GetConfigAsync(CancellationToken cancellationToken)
        => await _db.TelegramConfigs.AsNoTracking().FirstOrDefaultAsync(cancellationToken)
           ?? throw new InvalidOperationException("Telegram configuration not found in the database.");
}
