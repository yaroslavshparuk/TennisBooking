namespace TennisBooking.Domain.Booking;

public sealed record BookingUserConfig(
    int Id,
    string Username,
    string Password,
    string ResourceId,
    string Venue,
    string VenueUser,
    DayOfWeek DayOfWeek,
    int Hour);
