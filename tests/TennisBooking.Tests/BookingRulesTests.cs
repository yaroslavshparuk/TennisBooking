using TennisBooking.Domain.Booking;
using TennisBooking.Infrastructure.Scheduling;
using Xunit;

namespace TennisBooking.Tests;

public sealed class BookingRulesTests
{
    private static readonly TimeZoneInfo PlusTwo =
        TimeZoneInfo.CreateCustomTimeZone("Test+02", TimeSpan.FromHours(2), "Test+02", "Test+02");

    [Fact]
    public void CreateSlot_WrapsSameDay_WhenHourStillAhead()
    {
        // Monday 08:00 local, requesting Monday 10:00 -> today + 14 days.
        var now = new DateTimeOffset(2026, 5, 18, 6, 0, 0, TimeSpan.Zero); // 08:00 +02
        var config = TestConfig(DayOfWeek.Monday, 10);

        var slot = BookingRules.CreateSlotForNextBookableDate(config, now, PlusTwo);

        Assert.Equal(new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.FromHours(2)), slot.StartTime);
    }

    [Fact]
    public void CreateSlot_WrapsToNextWeek_WhenRequestedDayAlreadyPassed()
    {
        // Wednesday local, requesting Monday -> 5 days out + 14.
        var now = new DateTimeOffset(2026, 5, 20, 6, 0, 0, TimeSpan.Zero); // Wed 08:00 +02
        var config = TestConfig(DayOfWeek.Monday, 10);

        var slot = BookingRules.CreateSlotForNextBookableDate(config, now, PlusTwo);

        Assert.Equal(DayOfWeek.Monday, TimeZoneInfo.ConvertTime(slot.StartTime, PlusTwo).DayOfWeek);
        Assert.Equal(
            TimeZoneInfo.ConvertTime(now, PlusTwo).Date.AddDays(5 + 14),
            TimeZoneInfo.ConvertTime(slot.StartTime, PlusTwo).Date);
    }

    [Fact]
    public void CreateSlot_MapsHour_Exactly()
    {
        var now = new DateTimeOffset(2026, 5, 18, 6, 0, 0, TimeSpan.Zero);
        var config = TestConfig(DayOfWeek.Monday, 18);

        var slot = BookingRules.CreateSlotForNextBookableDate(config, now, PlusTwo);

        Assert.Equal(18, TimeZoneInfo.ConvertTime(slot.StartTime, PlusTwo).Hour);
        Assert.Equal(0, TimeZoneInfo.ConvertTime(slot.StartTime, PlusTwo).Minute);
    }

    [Fact]
    public void BookingSlot_DerivesEndTimeAndOpensAt()
    {
        var start = new DateTimeOffset(2030, 6, 15, 10, 0, 0, TimeSpan.Zero);
        var slot = new BookingSlot(start);

        Assert.Equal(start.AddHours(1), slot.EndTime);
        Assert.Equal(start.AddDays(-14), slot.BookingOpensAt);
    }

    [Fact]
    public void BookingOpensAt_CrossesAutumnDstTransition_AtSameWallTime()
    {
        // Slot Wed Nov 4 2026 19:00 Kyiv (winter, +02). Booking opens Oct 21 19:00 Kyiv
        // (summer, +03) = 16:00Z. The naive StartTime.AddDays(-14) keeps +02 and lands on
        // Oct 21 17:00Z = 20:00 Kyiv: one hour late, a complete miss.
        var kyiv = KyivZoneOrSkip();
        if (kyiv is null)
            return;

        var start = new DateTimeOffset(2026, 11, 4, 19, 0, 0, kyiv.GetUtcOffset(new DateTime(2026, 11, 4, 19, 0, 0)));
        var slot = new BookingSlot(start);

        var expected = new DateTimeOffset(2026, 10, 21, 19, 0, 0, kyiv.GetUtcOffset(new DateTime(2026, 10, 21, 19, 0, 0)));
        Assert.Equal(expected, slot.BookingOpensAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 16, 0, 0, TimeSpan.Zero), slot.BookingOpensAt.ToUniversalTime());
    }

    [Fact]
    public void BookingOpensAt_CrossesSpringDstTransition_AtSameWallTime()
    {
        // Slot Wed Apr 8 2026 19:00 Kyiv (summer, +03). Booking opens Mar 25 19:00 Kyiv
        // (winter, +02) = 17:00Z; naive math would give 16:00Z = 18:00 Kyiv.
        var kyiv = KyivZoneOrSkip();
        if (kyiv is null)
            return;

        var start = new DateTimeOffset(2026, 4, 8, 19, 0, 0, kyiv.GetUtcOffset(new DateTime(2026, 4, 8, 19, 0, 0)));
        var slot = new BookingSlot(start);

        var expected = new DateTimeOffset(2026, 3, 25, 19, 0, 0, kyiv.GetUtcOffset(new DateTime(2026, 3, 25, 19, 0, 0)));
        Assert.Equal(expected, slot.BookingOpensAt);
        Assert.Equal(new DateTimeOffset(2026, 3, 25, 17, 0, 0, TimeSpan.Zero), slot.BookingOpensAt.ToUniversalTime());
    }

    private static TimeZoneInfo? KyivZoneOrSkip()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");
        }
        catch
        {
            return null; // no TZDB on this host; servers and CI have it
        }
    }

    [Fact]
    public void DeduplicationStore_RejectsDuplicate_UntilReleased()
    {
        var store = new InMemoryBookingDeduplicationStore();

        Assert.True(store.TryBegin("k"));
        Assert.False(store.TryBegin("k"));

        store.Release("k");

        Assert.True(store.TryBegin("k"));
    }

    [Fact]
    public void DeduplicationStore_AllowsExactlyOneWinner_UnderConcurrency()
    {
        var store = new InMemoryBookingDeduplicationStore();
        var wins = 0;
        Parallel.For(0, 16, _ =>
        {
            if (store.TryBegin("race"))
                Interlocked.Increment(ref wins);
        });

        Assert.Equal(1, wins);
    }

    private static BookingUserConfig TestConfig(DayOfWeek day, int hour)
        => new(1, "u", "p", "r", "v", "vu", day, hour);
}
