namespace TennisBooking.DAL.Models;

public class BookingCancellationLinkEntity
{
    public long Id { get; set; }
    public int UserConfigId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string Venue { get; set; } = string.Empty;
    public string VenueUser { get; set; } = string.Empty;
    public DayOfWeek DayOfWeek { get; set; }
    public int Hour { get; set; }
    public DateTimeOffset SlotStartUtc { get; set; }
    public DateTimeOffset SlotEndUtc { get; set; }
    public long ChatId { get; set; }
    public int TelegramMessageId { get; set; }
    public string SkeddaBookingId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? CancelledAtUtc { get; set; }
    public int? CancelRequestMessageId { get; set; }
    public DateTimeOffset? AttendanceReminder24hSentAtUtc { get; set; }
    public DateTimeOffset? AttendanceReminder2hSentAtUtc { get; set; }
}
