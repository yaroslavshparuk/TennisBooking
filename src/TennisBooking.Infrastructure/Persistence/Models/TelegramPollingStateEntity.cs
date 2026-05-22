namespace TennisBooking.DAL.Models;

public class TelegramPollingStateEntity
{
    public int Id { get; set; }
    public long LastProcessedUpdateId { get; set; }
}
