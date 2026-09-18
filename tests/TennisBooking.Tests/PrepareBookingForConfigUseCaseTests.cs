using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TennisBooking.Application.Abstractions;
using TennisBooking.Application.Booking;
using TennisBooking.Domain.Booking;
using Xunit;

namespace TennisBooking.Tests;

public sealed class PrepareBookingForConfigUseCaseTests
{
    [Fact]
    public async Task ExecuteAsync_LoadsConfigAndDelegates_WithScheduleFlag()
    {
        var user = new BookingUserConfig(7, "u", "p", "r", "v", "vu", DayOfWeek.Monday, 10);
        var slot = new BookingSlot(new DateTimeOffset(2030, 6, 1, 7, 0, 0, TimeSpan.Zero));
        var prepared = new PreparedBooking(user, slot, "{}", "cookies", "t", "c", "a");
        var repos = new Mock<IUserBookingConfigRepository>();
        repos.Setup(x => x.GetByIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        var skedda = new Mock<ISkeddaClient>();
        skedda.Setup(x => x.PrepareBookingAsync(user, It.IsAny<BookingSlot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(prepared);
        var scheduler = new Mock<IBookingScheduler>();
        var inner = new PrepareBookingUseCase(
            skedda.Object, scheduler.Object, new FixedTestClock(new DateTimeOffset(2026, 5, 19, 12, 0, 0, TimeSpan.Zero)));
        var useCase = new PrepareBookingForConfigUseCase(repos.Object, inner);

        await useCase.ExecuteAsync(7, true, CancellationToken.None);

        skedda.Verify(
            x => x.PrepareBookingAsync(user, It.IsAny<BookingSlot>(), It.IsAny<CancellationToken>()),
            Times.Once);
        scheduler.Verify(x => x.SchedulePreciseBooking(prepared), Times.Once);
        scheduler.Verify(
            x => x.ScheduleFallback(user.Id, slot.StartTime, It.IsAny<DateTimeOffset>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_PassesScheduleFalseThrough_WithoutScheduling()
    {
        var user = new BookingUserConfig(7, "u", "p", "r", "v", "vu", DayOfWeek.Monday, 10);
        var slot = new BookingSlot(new DateTimeOffset(2030, 6, 1, 7, 0, 0, TimeSpan.Zero));
        var prepared = new PreparedBooking(user, slot, "{}", "cookies", "t", "c", "a");
        var repos = new Mock<IUserBookingConfigRepository>();
        repos.Setup(x => x.GetByIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        var skedda = new Mock<ISkeddaClient>();
        skedda.Setup(x => x.PrepareBookingAsync(user, It.IsAny<BookingSlot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(prepared);
        var scheduler = new Mock<IBookingScheduler>(MockBehavior.Strict);
        var inner = new PrepareBookingUseCase(
            skedda.Object, scheduler.Object, new FixedTestClock(new DateTimeOffset(2026, 5, 19, 12, 0, 0, TimeSpan.Zero)));
        var useCase = new PrepareBookingForConfigUseCase(repos.Object, inner);

        await useCase.ExecuteAsync(7, false, CancellationToken.None);

        skedda.Verify(
            x => x.PrepareBookingAsync(user, It.IsAny<BookingSlot>(), It.IsAny<CancellationToken>()),
            Times.Once);
        scheduler.Verify(
            x => x.SchedulePreciseBooking(It.IsAny<PreparedBooking>()),
            Times.Never);
        scheduler.Verify(
            x => x.ScheduleFallback(It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_Throws_WhenConfigMissing()
    {
        var repos = new Mock<IUserBookingConfigRepository>();
        repos.Setup(x => x.GetByIdAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync((BookingUserConfig?)null);
        var inner = new PrepareBookingUseCase(
            Mock.Of<ISkeddaClient>(),
            Mock.Of<IBookingScheduler>(),
            new FixedTestClock(new DateTimeOffset(2026, 5, 19, 12, 0, 0, TimeSpan.Zero)));
        var useCase = new PrepareBookingForConfigUseCase(repos.Object, inner);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => useCase.ExecuteAsync(42, true, CancellationToken.None));
        Assert.Contains("42", ex.Message);
    }

    private sealed class FixedTestClock : IClock
    {
        public FixedTestClock(DateTimeOffset utcNow) => UtcNow = utcNow;
        public DateTimeOffset UtcNow { get; }
    }
}
