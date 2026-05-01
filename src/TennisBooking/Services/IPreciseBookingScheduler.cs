namespace TennisBooking.Services;

using Models;

public interface IPreciseBookingScheduler
{
    void ScheduleBooking(BookingInfo bookingInfo);
}
