using TennisBooking.Application.Booking;
using TennisBooking.Domain.Booking;

namespace TennisBooking.Application.Abstractions;

public interface IBookingScheduler
{
    void SchedulePreciseBooking(PreparedBooking booking);
    void ScheduleFallback(int userConfigId, DateTimeOffset startTime, DateTimeOffset runAt);
    void ScheduleAttendanceCheck(long chatId, int telegramMessageId, DateTimeOffset slotStartUtc, string reminderType, DateTimeOffset runAtUtc);
    void ScheduleRecurringPreparation(BookingUserConfig userConfig);
}
