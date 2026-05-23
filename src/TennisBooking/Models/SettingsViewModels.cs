using TennisBooking.Domain.Booking;

namespace TennisBooking.Models;

public sealed record SettingsIndexViewModel(IReadOnlyList<UserConfigScheduleViewModel> UserConfigs);

public sealed record UserConfigScheduleViewModel(
    int Id,
    string Username,
    string ResourceId,
    string Venue,
    string VenueUser,
    DayOfWeek DayOfWeek,
    int Hour,
    string? Message = null,
    bool IsError = false)
{
    public static UserConfigScheduleViewModel FromDomain(
        BookingUserConfig config,
        string? message = null,
        bool isError = false)
        => new(
            config.Id,
            config.Username,
            config.ResourceId,
            config.Venue,
            config.VenueUser,
            config.DayOfWeek,
            config.Hour,
            message,
            isError);
}
