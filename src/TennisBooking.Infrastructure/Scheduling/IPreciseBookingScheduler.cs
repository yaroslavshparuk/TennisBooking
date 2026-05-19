using TennisBooking.Application.Booking;

namespace TennisBooking.Infrastructure.Scheduling;

public interface IPreciseBookingScheduler
{
    void ScheduleBooking(PreparedBooking booking);
}
