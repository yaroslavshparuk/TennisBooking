using TennisBooking.Application.Booking;
using TennisBooking.Domain.Booking;

namespace TennisBooking.Application.Abstractions;

public interface IBookingScheduler
{
    void SchedulePreciseBooking(PreparedBooking booking);
    void ScheduleFallback(int userConfigId, DateTimeOffset startTime, DateTimeOffset runAt);
    void ScheduleRecurringPreparation(BookingUserConfig userConfig);
}
