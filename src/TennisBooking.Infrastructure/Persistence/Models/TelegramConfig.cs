namespace TennisBooking.DAL.Models;

public class TelegramConfig
{
    public int Id { get; set; }
    public string BotToken { get; set; } = string.Empty;
    public long ChatId { get; set; }
}
