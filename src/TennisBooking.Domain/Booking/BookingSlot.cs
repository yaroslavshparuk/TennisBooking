namespace TennisBooking.Domain.Booking;

public sealed record BookingSlot(DateTimeOffset StartTime)
{
    public DateTimeOffset EndTime => StartTime.AddHours(1);

    // Skedda opens booking at the same wall-clock time 14 calendar days before the slot
    // in Europe/Kyiv. DateTimeOffset.AddDays(-14) keeps the future offset, so across a DST
    // transition it lands an hour off (e.g. Nov 4 19:00+02 minus 14d = Oct 21 19:00+02 = 20:00 Kyiv
    // summer time instead of 19:00). Re-resolve the offset for the opening date.
    public DateTimeOffset BookingOpensAt => ResolveOpensAt(StartTime);

    private static readonly TimeZoneInfo? KyivZone = LoadKyivZone();

    private static TimeZoneInfo? LoadKyivZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset ResolveOpensAt(DateTimeOffset start)
    {
        // No TZ data on this host: degrade to wall-clock-agnostic math instead of throwing.
        if (KyivZone is null)
            return start.AddDays(-14);

        var localStart = TimeZoneInfo.ConvertTime(start, KyivZone);
        var localOpens = localStart.AddDays(-14);
        return new DateTimeOffset(localOpens.DateTime, KyivZone.GetUtcOffset(localOpens.DateTime));
    }
}
