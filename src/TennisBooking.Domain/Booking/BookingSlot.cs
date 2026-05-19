namespace TennisBooking.Domain.Booking;

public sealed record BookingSlot(DateTimeOffset StartTime)
{
    public DateTimeOffset EndTime => StartTime.AddHours(1);
    public DateTimeOffset BookingOpensAt => StartTime.AddDays(-14);
}
