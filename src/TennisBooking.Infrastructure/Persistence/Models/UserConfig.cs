namespace TennisBooking.DAL.Models;

public class UserConfig
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string Venue { get; set; } = string.Empty;
    public string VenueUser { get; set; } = string.Empty;
    public DayOfWeek DayOfWeek { get; set; }
    public int Hour { get; set; }
}
