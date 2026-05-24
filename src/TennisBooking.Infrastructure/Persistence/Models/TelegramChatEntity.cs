namespace TennisBooking.DAL.Models;

public class TelegramChatEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public long ChatId { get; set; }
    public bool IsActive { get; set; }
}
