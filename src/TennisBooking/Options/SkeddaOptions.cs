namespace TennisBooking.Options;

public class SkeddaOptions {
  public string ApiBaseUrl       { get; set; }
  public string Username         { get; set; }
  public string Password         { get; set; }
  public string ResourceId       { get; set; }
  public string Venue            { get; set; }
  public string VenueUser        { get; set; }
  public DayOfWeek DayOfWeek     { get; set; }
  public TimeSpan BookingTime    { get; set; }
  public double DurationHours    { get; set; }
  public int BookingWindowDays   { get; set; }
}
