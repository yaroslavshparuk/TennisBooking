using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TennisBooking.DAL;
using TennisBooking.DAL.Models;

namespace TennisBooking.Services;

public class TelegramService {
    private readonly HttpClient _http;
    private readonly ApplicationDbContext _db;
    private readonly ILogger<TelegramService> _logger;

    private const string BaseUrlPrefix = "https://api.telegram.org/bot";

    public TelegramService(
        HttpClient http,
        ApplicationDbContext db,
        ILogger<TelegramService> logger) {
        _http = http;
        _db = db;
        _logger = logger;
    }

    public async Task NotifyAsync(string message) {
        try {
            var cfg = await GetConfigAsync();
            var url = $"{BaseUrlPrefix}{cfg.BotToken}/sendMessage";

            var payload = new { chat_id = cfg.ChatId, text = message, parse_mode = "MarkdownV2" };

            var content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");

            var resp = await _http.PostAsync(url, content);
            resp.EnsureSuccessStatusCode();
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Failed to send Telegram notification");
        }
    }

    private async Task<TelegramConfig> GetConfigAsync() {

        return await _db.TelegramConfigs
                   .AsNoTracking()
                   .FirstOrDefaultAsync()
               ?? throw new InvalidOperationException("Telegram configuration not found in the database.");
    }
}
