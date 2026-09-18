using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TennisBooking.Application.Abstractions;
using TennisBooking.Application.Booking;
using TennisBooking.Domain.Booking;
using Xunit;

namespace TennisBooking.Tests;

public sealed class ScheduleBookingsUseCaseTests
{
    [Fact]
    public async Task ExecuteAsync_WithNoConfigs_SchedulesNothing()
    {
        var repos = new Mock<IUserBookingConfigRepository>();
        repos.Setup(x => x.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<BookingUserConfig>());
        var scheduler = new Mock<IBookingScheduler>(MockBehavior.Strict);
        var useCase = new ScheduleBookingsUseCase(repos.Object, scheduler.Object);

        await useCase.ExecuteAsync(CancellationToken.None);

        scheduler.Verify(
            x => x.ScheduleRecurringPreparation(It.IsAny<BookingUserConfig>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_FansOutToEveryConfig_WithExactInstances()
    {
        var first = new BookingUserConfig(1, "u1", "p", "r1", "v", "vu", DayOfWeek.Monday, 10);
        var second = new BookingUserConfig(2, "u2", "p", "r2", "v", "vu", DayOfWeek.Wednesday, 18);
        var repos = new Mock<IUserBookingConfigRepository>();
        repos.Setup(x => x.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { first, second });
        var scheduled = new List<BookingUserConfig>();
        var scheduler = new Mock<IBookingScheduler>();
        scheduler.Setup(x => x.ScheduleRecurringPreparation(It.IsAny<BookingUserConfig>()))
            .Callback<BookingUserConfig>(scheduled.Add);
        var useCase = new ScheduleBookingsUseCase(repos.Object, scheduler.Object);

        await useCase.ExecuteAsync(CancellationToken.None);

        Assert.Equal(new[] { first, second }, scheduled);
    }
}
