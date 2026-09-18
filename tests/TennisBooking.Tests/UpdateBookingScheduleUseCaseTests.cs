using Moq;
using TennisBooking.Application.Abstractions;
using TennisBooking.Application.Booking;
using TennisBooking.Domain.Booking;
using Xunit;

namespace TennisBooking.Tests;

public sealed class UpdateBookingScheduleUseCaseTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(7)]
    [InlineData(42)]
    public async Task ExecuteAsync_RejectsInvalidDayOfWeek_WithoutTouchingDb(int dayOfWeek)
    {
        var repos = new Mock<IUserBookingConfigRepository>(MockBehavior.Strict);
        var scheduler = new Mock<IBookingScheduler>(MockBehavior.Strict);
        var useCase = new UpdateBookingScheduleUseCase(repos.Object, scheduler.Object);

        var result = await useCase.ExecuteAsync(1, dayOfWeek, 10, CancellationToken.None);

        Assert.Equal(UpdateBookingScheduleStatus.Invalid, result.Status);
        Assert.Null(result.UserConfig);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        repos.Verify(
            x => x.UpdateScheduleAsync(It.IsAny<int>(), It.IsAny<DayOfWeek>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        scheduler.Verify(
            x => x.ScheduleRecurringPreparation(It.IsAny<BookingUserConfig>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsNotFound_WhenRepoHasNoConfig()
    {
        var repos = new Mock<IUserBookingConfigRepository>();
        repos.Setup(x => x.UpdateScheduleAsync(9, DayOfWeek.Friday, 18, It.IsAny<CancellationToken>()))
            .ReturnsAsync((BookingUserConfig?)null);
        var scheduler = new Mock<IBookingScheduler>(MockBehavior.Strict);
        var useCase = new UpdateBookingScheduleUseCase(repos.Object, scheduler.Object);

        var result = await useCase.ExecuteAsync(9, (int)DayOfWeek.Friday, 18, CancellationToken.None);

        Assert.Equal(UpdateBookingScheduleStatus.NotFound, result.Status);
        scheduler.Verify(
            x => x.ScheduleRecurringPreparation(It.IsAny<BookingUserConfig>()),
            Times.Never);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(23)]
    public async Task ExecuteAsync_AcceptsHourBoundaries(int hour)
    {
        var updated = new BookingUserConfig(1, "u", "p", "r", "v", "vu", DayOfWeek.Tuesday, hour);
        var repos = new Mock<IUserBookingConfigRepository>();
        repos.Setup(x => x.UpdateScheduleAsync(1, DayOfWeek.Tuesday, hour, It.IsAny<CancellationToken>()))
            .ReturnsAsync(updated);
        BookingUserConfig? rescheduled = null;
        var scheduler = new Mock<IBookingScheduler>();
        scheduler.Setup(x => x.ScheduleRecurringPreparation(It.IsAny<BookingUserConfig>()))
            .Callback<BookingUserConfig>(c => rescheduled = c);
        var useCase = new UpdateBookingScheduleUseCase(repos.Object, scheduler.Object);

        var result = await useCase.ExecuteAsync(1, (int)DayOfWeek.Tuesday, hour, CancellationToken.None);

        Assert.Equal(UpdateBookingScheduleStatus.Updated, result.Status);
        Assert.Same(updated, result.UserConfig);
        Assert.Same(updated, rescheduled);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(24)]
    public async Task ExecuteAsync_RejectsOutOfRangeHours(int hour)
    {
        var repos = new Mock<IUserBookingConfigRepository>(MockBehavior.Strict);
        var scheduler = new Mock<IBookingScheduler>(MockBehavior.Strict);
        var useCase = new UpdateBookingScheduleUseCase(repos.Object, scheduler.Object);

        var result = await useCase.ExecuteAsync(1, (int)DayOfWeek.Monday, hour, CancellationToken.None);

        Assert.Equal(UpdateBookingScheduleStatus.Invalid, result.Status);
    }

    [Theory]
    [InlineData(0, DayOfWeek.Sunday)]
    [InlineData(6, DayOfWeek.Saturday)]
    [InlineData(3, DayOfWeek.Wednesday)]
    public async Task ExecuteAsync_MapsValidDayInts_ToDayOfWeek(int dayOfWeek, DayOfWeek expected)
    {
        const int hour = 10;
        var updated = new BookingUserConfig(1, "u", "p", "r", "v", "vu", expected, hour);
        var repos = new Mock<IUserBookingConfigRepository>();
        repos.Setup(x => x.UpdateScheduleAsync(1, expected, hour, It.IsAny<CancellationToken>()))
            .ReturnsAsync(updated);
        var scheduler = new Mock<IBookingScheduler>();
        var useCase = new UpdateBookingScheduleUseCase(repos.Object, scheduler.Object);

        var result = await useCase.ExecuteAsync(1, dayOfWeek, hour, CancellationToken.None);

        Assert.Equal(UpdateBookingScheduleStatus.Updated, result.Status);
        Assert.Equal(expected, result.UserConfig!.DayOfWeek);
        repos.Verify(x => x.UpdateScheduleAsync(1, expected, hour, It.IsAny<CancellationToken>()), Times.Once);
    }
}
