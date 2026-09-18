using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TennisBooking.Application.Abstractions;
using TennisBooking.Application.Booking;
using TennisBooking.Domain.Booking;
using TennisBooking.Infrastructure.Scheduling;
using Xunit;

namespace TennisBooking.Tests;

public sealed class ExecuteBookingUseCaseTests
{
    [Fact]
    public async Task ExecuteAsync_WithNull_DoesNothing()
    {
        var skedda = new Mock<ISkeddaClient>(MockBehavior.Strict);
        var dedup = new Mock<IBookingDeduplicationStore>(MockBehavior.Strict);
        var useCase = NewUseCase(skedda.Object, dedup.Object);

        await useCase.ExecuteAsync(null, CancellationToken.None);

        skedda.Verify(
            x => x.BookAsync(It.IsAny<PreparedBooking>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        dedup.Verify(x => x.TryBegin(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ReleasesDedupKey_WhenBookThrows_SoFallbackCanRetry()
    {
        var booking = TestBooking();
        // Real store (not a stubbed TryBegin): the retry below can only re-post
        // if the first ExecuteAsync actually released the key.
        var dedup = new InMemoryBookingDeduplicationStore();
        var skedda = new Mock<ISkeddaClient>();
        var failure = new InvalidOperationException("boom");
        skedda.SetupSequence(x => x.BookAsync(booking, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure)
            .ReturnsAsync(new SkeddaBookingResult("ok"));
        var notification = new Mock<INotificationSender>();
        notification.Setup(x => x.NotifyBookingSucceededAsync(
                It.IsAny<BookingUserConfig>(), It.IsAny<BookingSlot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TelegramNotificationResult(0, 0)); // skip link/reminder follow-ups
        var useCase = NewUseCase(skedda.Object, dedup, notification.Object);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => useCase.ExecuteAsync(booking, CancellationToken.None));
        Assert.Same(failure, thrown);

        await useCase.ExecuteAsync(booking, CancellationToken.None);
        skedda.Verify(
            x => x.BookAsync(booking, It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task ExecuteAsync_SchedulesReminders_AtExactSlotMinus24And2HoursUtc()
    {
        var slotStart = new DateTimeOffset(2030, 6, 15, 10, 0, 0, TimeSpan.FromHours(3));
        var booking = TestBooking(slotStart);
        var runs = new List<(string Type, DateTimeOffset RunAt, DateTimeOffset SlotUtc)>();
        var scheduler = new Mock<IBookingScheduler>();
        scheduler.Setup(x => x.ScheduleAttendanceCheck(
                It.IsAny<long>(), It.IsAny<int>(), It.IsAny<DateTimeOffset>(),
                It.IsAny<string>(), It.IsAny<DateTimeOffset>()))
            .Callback<long, int, DateTimeOffset, string, DateTimeOffset>(
                (_, _, slotUtc, type, runAt) => runs.Add((type, runAt, slotUtc)))
            .Returns("job");
        var notification = new Mock<INotificationSender>();
        notification.Setup(x => x.NotifyBookingSucceededAsync(
                It.IsAny<BookingUserConfig>(), It.IsAny<BookingSlot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TelegramNotificationResult(5, 10));
        var links = new Mock<IBookingCancellationLinkRepository>();
        links.Setup(x => x.SaveReminderJobIdAsync(
                It.IsAny<long>(), It.IsAny<int>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var skedda = new Mock<ISkeddaClient>();
        skedda.Setup(x => x.BookAsync(booking, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SkeddaBookingResult("b1"));
        var useCase = NewUseCase(
            skedda.Object,
            new InMemoryBookingDeduplicationStore(),
            notification.Object,
            links.Object,
            scheduler.Object);

        await useCase.ExecuteAsync(booking, CancellationToken.None);

        var slotUtc = slotStart.ToUniversalTime();
        Assert.Equal(2, runs.Count);
        Assert.Contains(runs, r =>
            r.Type == AttendanceReminderUseCase.ReminderType24h &&
            r.RunAt == slotUtc.AddHours(-24) &&
            r.SlotUtc == slotUtc);
        Assert.Contains(runs, r =>
            r.Type == AttendanceReminderUseCase.ReminderType2h &&
            r.RunAt == slotUtc.AddHours(-2) &&
            r.SlotUtc == slotUtc);
    }

    [Fact]
    public async Task TryBookOnce_InvokesOnBooked_ImmediatelyOnWin()
    {
        var booking = TestBooking();
        var skedda = new Mock<ISkeddaClient>();
        skedda.Setup(x => x.BookAsync(booking, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SkeddaBookingResult("b1"));
        var notification = new Mock<INotificationSender>();
        notification.Setup(x => x.NotifyBookingSucceededAsync(
                It.IsAny<BookingUserConfig>(), It.IsAny<BookingSlot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TelegramNotificationResult(0, 0));
        var useCase = NewUseCase(skedda.Object, new InMemoryBookingDeduplicationStore(), notification.Object);
        var invoked = 0;

        var won = await useCase.TryBookOnceAsync(
            booking, 0, 0, CancellationToken.None, CancellationToken.None, () => invoked++);

        Assert.True(won);
        Assert.Equal(1, invoked);
    }

    [Fact]
    public async Task TryBookOnce_DoesNotInvokeOnBooked_WhenRejected()
    {
        var booking = TestBooking();
        var skedda = new Mock<ISkeddaClient>();
        skedda.Setup(x => x.BookAsync(booking, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SkeddaBookingRejectedException(409, "taken"));
        var useCase = NewUseCase(skedda.Object, new InMemoryBookingDeduplicationStore());
        var invoked = 0;

        var won = await useCase.TryBookOnceAsync(
            booking, 0, 0, CancellationToken.None, CancellationToken.None, () => invoked++);

        Assert.False(won);
        Assert.Equal(0, invoked);
    }

    [Fact]
    public async Task TryBookOnce_DoesNotInvokeOnBooked_WhenBookFails()
    {
        var booking = TestBooking();
        var skedda = new Mock<ISkeddaClient>();
        skedda.Setup(x => x.BookAsync(booking, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("boom"));
        var useCase = NewUseCase(skedda.Object, new InMemoryBookingDeduplicationStore());
        var invoked = 0;

        var won = await useCase.TryBookOnceAsync(
            booking, 0, 0, CancellationToken.None, CancellationToken.None, () => invoked++);

        Assert.False(won);
        Assert.Equal(0, invoked);
    }

    [Fact]
    public async Task TryBookOnce_Rethrows_WhenSendIsCancelled()
    {
        var booking = TestBooking();
        var skedda = new Mock<ISkeddaClient>();
        skedda.Setup(x => x.BookAsync(
                It.IsAny<PreparedBooking>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns<PreparedBooking, int, CancellationToken>((_, _, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(new SkeddaBookingResult("b1"));
            });
        var notification = new Mock<INotificationSender>(MockBehavior.Strict);
        var links = new Mock<IBookingCancellationLinkRepository>(MockBehavior.Strict);
        var useCase = NewUseCase(skedda.Object, new InMemoryBookingDeduplicationStore(), notification.Object, links.Object);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => useCase.TryBookOnceAsync(
            booking, 0, 0, cts.Token, CancellationToken.None));

        notification.Verify(
            x => x.NotifyBookingSucceededAsync(
                It.IsAny<BookingUserConfig>(), It.IsAny<BookingSlot>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static PreparedBooking TestBooking(
        DateTimeOffset? start = null)
    {
        var user = new BookingUserConfig(1, "u", "p", "r", "v", "vu", DayOfWeek.Monday, 10);
        return new PreparedBooking(
            user,
            new BookingSlot(start ?? new DateTimeOffset(2030, 6, 15, 10, 0, 0, TimeSpan.Zero)),
            "{}", "cookies", "t", "c", "a");
    }

    private static ExecuteBookingUseCase NewUseCase(
        ISkeddaClient skedda,
        IBookingDeduplicationStore dedup,
        INotificationSender? notification = null,
        IBookingCancellationLinkRepository? links = null,
        IBookingScheduler? scheduler = null)
        => new(
            skedda,
            notification ?? Mock.Of<INotificationSender>(),
            dedup,
            links ?? Mock.Of<IBookingCancellationLinkRepository>(),
            scheduler ?? Mock.Of<IBookingScheduler>(),
            NullLogger<ExecuteBookingUseCase>.Instance);
}
