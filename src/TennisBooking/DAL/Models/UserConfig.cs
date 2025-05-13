namespace TennisBooking.DAL.Models;

public class UserConfig {
    public int Id { get; set; }
    public string Username { get; set; }
    public string Password { get; set; }
    public string ResourceId { get; set; }
    public string Venue { get; set; }
    public string VenueUser { get; set; }
    public DayOfWeek DayOfWeek { get; set; }
    public int Hour { get; set; }
}
