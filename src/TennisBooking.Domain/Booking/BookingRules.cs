namespace TennisBooking.Domain.Booking;

public static class BookingRules
{
    public static BookingSlot CreateSlotForNextBookableDate(
        BookingUserConfig userConfig,
        DateTimeOffset utcNow,
        TimeZoneInfo timeZone)
    {
        var localNow = TimeZoneInfo.ConvertTime(utcNow, timeZone);
        var daysUntilRequestedDay = ((int)userConfig.DayOfWeek - (int)localNow.DayOfWeek + 7) % 7;
        var bookingDate = localNow.Date.AddDays(daysUntilRequestedDay + 14);
        var localStart = new DateTime(
            bookingDate.Year,
            bookingDate.Month,
            bookingDate.Day,
            userConfig.Hour,
            0,
            0,
            DateTimeKind.Unspecified);

        return new BookingSlot(new DateTimeOffset(localStart, timeZone.GetUtcOffset(localStart)));
    }
}
