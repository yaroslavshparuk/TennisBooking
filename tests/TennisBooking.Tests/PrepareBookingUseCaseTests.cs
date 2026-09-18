using Moq;
using TennisBooking.Application.Abstractions;
using TennisBooking.Application.Booking;
using TennisBooking.Domain.Booking;
using Xunit;

namespace TennisBooking.Tests;

public sealed class PrepareBookingUseCaseTests
{
    [Fact]
    public async Task ExecuteAsync_WithScheduleFalse_SchedulesNothing_ButReturnsBooking()
    {
        var user = new BookingUserConfig(1, "u", "p", "r", "v", "vu", DayOfWeek.Monday, 10);
        var prepared = TestPrepared(user);
        var skedda = new Mock<ISkeddaClient>();
        skedda.Setup(x => x.PrepareBookingAsync(user, It.IsAny<BookingSlot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(prepared);
        var scheduler = new Mock<IBookingScheduler>(MockBehavior.Strict);
        var useCase = new PrepareBookingUseCase(
            skedda.Object, scheduler.Object, new FixedTestClock(Utc(2026, 5, 19, 12)));

        var result = await useCase.ExecuteAsync(user, false, CancellationToken.None);

        Assert.Same(prepared, result);
        scheduler.Verify(
            x => x.SchedulePreciseBooking(It.IsAny<PreparedBooking>()), Times.Never);
        scheduler.Verify(
            x => x.ScheduleFallback(It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_SchedulesFallback_AtBookingOpensPlusThreeSeconds()
    {
        // Tuesday 2026-05-19 12:00 UTC: next Monday is 6 days out, so opens-at is in the future.
        var now = Utc(2026, 5, 19, 12);
        var user = new BookingUserConfig(1, "u", "p", "r", "v", "vu", DayOfWeek.Monday, 10);
        DateTimeOffset? capturedRunAt = null;
        var scheduler = new Mock<IBookingScheduler>();
        scheduler.Setup(x => x.ScheduleFallback(It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>()))
            .Callback<int, DateTimeOffset, DateTimeOffset>((_, _, runAt) => capturedRunAt = runAt);
        var skedda = new Mock<ISkeddaClient>();
        skedda.Setup(x => x.PrepareBookingAsync(user, It.IsAny<BookingSlot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((BookingUserConfig u, BookingSlot s, CancellationToken _) =>
                new PreparedBooking(u, s, "{}", "c", "t", "v", "a"));
        var useCase = new PrepareBookingUseCase(skedda.Object, scheduler.Object, new FixedTestClock(now));

        var result = await useCase.ExecuteAsync(user, true, CancellationToken.None);

        Assert.NotNull(result);
        var expected = result!.Slot.BookingOpensAt.AddSeconds(3);
        Assert.True(expected > now, "Fixture must exercise the non-clamped path.");
        Assert.Equal(expected, capturedRunAt);
    }

    [Fact]
    public async Task ExecuteAsync_ClampsFallback_ToNowPlusThreeSeconds_WhenOpensAtIsPast()
    {
        // Monday 12:00 Kyiv, requesting Monday 10:00: same-day wrap puts opens-at 2h in the past.
        var now = new DateTimeOffset(2026, 5, 18, 9, 0, 0, TimeSpan.Zero); // 12:00 Kyiv (EEST)
        var user = new BookingUserConfig(1, "u", "p", "r", "v", "vu", DayOfWeek.Monday, 10);
        DateTimeOffset? capturedRunAt = null;
        var scheduler = new Mock<IBookingScheduler>();
        scheduler.Setup(x => x.ScheduleFallback(It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>()))
            .Callback<int, DateTimeOffset, DateTimeOffset>((_, _, runAt) => capturedRunAt = runAt);
        var skedda = new Mock<ISkeddaClient>();
        skedda.Setup(x => x.PrepareBookingAsync(user, It.IsAny<BookingSlot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((BookingUserConfig u, BookingSlot s, CancellationToken _) =>
                new PreparedBooking(u, s, "{}", "c", "t", "v", "a"));
        var useCase = new PrepareBookingUseCase(skedda.Object, scheduler.Object, new FixedTestClock(now));

        var result = await useCase.ExecuteAsync(user, true, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.Slot.BookingOpensAt.AddSeconds(3) < now, "Fixture must exercise the clamped path.");
        Assert.Equal(now.AddSeconds(3), capturedRunAt);
    }

    private static PreparedBooking TestPrepared(BookingUserConfig user)
        => new(
            user,
            new BookingSlot(new DateTimeOffset(2030, 6, 1, 7, 0, 0, TimeSpan.Zero)),
            "{}", "cookies", "t", "c", "a");

    private static DateTimeOffset Utc(int y, int m, int d, int h)
        => new(y, m, d, h, 0, 0, TimeSpan.Zero);

    private sealed class FixedTestClock : IClock
    {
        public FixedTestClock(DateTimeOffset utcNow) => UtcNow = utcNow;
        public DateTimeOffset UtcNow { get; }
    }
}
