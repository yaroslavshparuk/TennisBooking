using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TennisBooking.Application.Abstractions;
using TennisBooking.Application.Booking;
using TennisBooking.Domain.Booking;
using Xunit;

namespace TennisBooking.Tests;

public sealed class CancelBookingUseCaseTests
{
    [Fact]
    public async Task ExecuteAsync_ReturnsNotFound_WhenLinkIsMissing()
    {
        var links = new Mock<IBookingCancellationLinkRepository>();
        links.Setup(x => x.GetByReplyAsync(5, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync((BookingCancellationLink?)null);
        var skedda = new Mock<ISkeddaClient>(MockBehavior.Strict);
        var scheduler = new Mock<IBookingScheduler>(MockBehavior.Strict);
        var useCase = NewUseCase(links.Object, skedda.Object, scheduler.Object);

        var status = await useCase.ExecuteAsync(5, 10, 99, CancellationToken.None);

        Assert.Equal(CancelBookingStatus.NotFound, status);
        skedda.Verify(
            x => x.CancelAsync(It.IsAny<PreparedBooking>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        scheduler.Verify(
            x => x.DeleteAttendanceCheck(It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsAlreadyCancelled_AndStillDeletesReminders_WhenMarkLosesRace()
    {
        var link = TestLink("job-24", "job-2");
        var links = new Mock<IBookingCancellationLinkRepository>();
        links.Setup(x => x.GetByReplyAsync(5, 10, It.IsAny<CancellationToken>())).ReturnsAsync(link);
        links.Setup(x => x.TryMarkCancelledAsync(5, 10, 99, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false); // sibling /cancel won the latch first
        var skedda = new Mock<ISkeddaClient>();
        var deleted = new List<string>();
        var scheduler = new Mock<IBookingScheduler>();
        scheduler.Setup(x => x.DeleteAttendanceCheck(It.IsAny<string>()))
            .Callback<string>(deleted.Add);
        var useCase = NewUseCase(links.Object, skedda.Object, scheduler.Object);

        var status = await useCase.ExecuteAsync(5, 10, 99, CancellationToken.None);

        Assert.Equal(CancelBookingStatus.AlreadyCancelled, status);
        skedda.Verify(
            x => x.CancelAsync(It.IsAny<PreparedBooking>(), "skedda-1", It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.Equal(new[] { "job-24", "job-2" }, deleted);
    }

    [Theory]
    [InlineData("job-24", null, "job-24")]
    [InlineData(null, "job-2", "job-2")]
    public async Task ExecuteAsync_DeletesOnlySetReminderJobs(string? job24, string? job2, string expectedDeleted)
    {
        var cancelled = TestLink(job24, job2) with { CancelledAtUtc = DateTimeOffset.UtcNow };
        var links = new Mock<IBookingCancellationLinkRepository>();
        links.Setup(x => x.GetByReplyAsync(5, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(cancelled);
        var skedda = new Mock<ISkeddaClient>(MockBehavior.Strict);
        var deleted = new List<string>();
        var scheduler = new Mock<IBookingScheduler>();
        scheduler.Setup(x => x.DeleteAttendanceCheck(It.IsAny<string>()))
            .Callback<string>(deleted.Add);
        var useCase = NewUseCase(links.Object, skedda.Object, scheduler.Object);

        var status = await useCase.ExecuteAsync(5, 10, 99, CancellationToken.None);

        Assert.Equal(CancelBookingStatus.AlreadyCancelled, status);
        Assert.Equal(new[] { expectedDeleted }, deleted);
    }

    [Fact]
    public async Task ExecuteAsync_IgnoresWhitespaceJobIds_WhenAlreadyCancelled()
    {
        var link = TestLink("  ", "job-2");
        var cancelled = link with { CancelledAtUtc = DateTimeOffset.UtcNow };
        var links = new Mock<IBookingCancellationLinkRepository>();
        links.Setup(x => x.GetByReplyAsync(5, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(cancelled);
        var skedda = new Mock<ISkeddaClient>(MockBehavior.Strict);
        var deleted = new List<string>();
        var scheduler = new Mock<IBookingScheduler>();
        scheduler.Setup(x => x.DeleteAttendanceCheck(It.IsAny<string>()))
            .Callback<string>(deleted.Add);
        var useCase = NewUseCase(links.Object, skedda.Object, scheduler.Object);

        var status = await useCase.ExecuteAsync(5, 10, 99, CancellationToken.None);

        Assert.Equal(CancelBookingStatus.AlreadyCancelled, status);
        Assert.Equal(new[] { "job-2" }, deleted);
    }

    private static BookingCancellationLink TestLink(string? job24, string? job2)
    {
        var user = new BookingUserConfig(1, "u", "p", "r", "v", "vu", DayOfWeek.Monday, 10);
        return new BookingCancellationLink(
            user,
            new BookingSlot(new DateTimeOffset(2030, 6, 15, 10, 0, 0, TimeSpan.Zero)),
            5,
            10,
            "skedda-1",
            DateTimeOffset.UtcNow,
            null,
            null,
            null,
            job24,
            job2);
    }

    private static CancelBookingUseCase NewUseCase(
        IBookingCancellationLinkRepository links,
        ISkeddaClient skedda,
        IBookingScheduler scheduler)
        => new(links, skedda, scheduler, NullLogger<CancelBookingUseCase>.Instance);
}
