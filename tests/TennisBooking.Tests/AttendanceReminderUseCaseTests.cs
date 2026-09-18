using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TennisBooking.Application.Abstractions;
using TennisBooking.Application.Booking;
using TennisBooking.Domain.Booking;
using Xunit;

namespace TennisBooking.Tests;

public sealed class AttendanceReminderUseCaseTests
{
    [Theory]
    [InlineData("3h")]
    [InlineData("")]
    [InlineData("24H")]
    public async Task ExecuteAsync_Throws_ForUnsupportedReminderType(string reminderType)
    {
        var useCase = NewUseCase();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => useCase.ExecuteAsync(5, 10, reminderType, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_Skips_WhenLinkIsMissing()
    {
        var links = new Mock<IBookingCancellationLinkRepository>();
        links.Setup(x => x.GetByMessageAsync(5, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync((BookingCancellationLink?)null);
        var notification = new Mock<INotificationSender>(MockBehavior.Strict);
        var weather = new Mock<IWeatherForecastProvider>(MockBehavior.Strict);
        var useCase = NewUseCase(links.Object, notification.Object, weather.Object);

        await useCase.ExecuteAsync(5, 10, AttendanceReminderUseCase.ReminderType24h, CancellationToken.None);

        notification.Verify(
            x => x.NotifyMessageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<int?>()),
            Times.Never);
        links.Verify(
            x => x.TryMarkReminderSentAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_Skips_WhenBookingWasCancelled()
    {
        var link = TestLink(cancelledAtUtc: DateTimeOffset.UtcNow);
        var links = new Mock<IBookingCancellationLinkRepository>();
        links.Setup(x => x.GetByMessageAsync(5, 10, It.IsAny<CancellationToken>())).ReturnsAsync(link);
        var notification = new Mock<INotificationSender>(MockBehavior.Strict);
        var useCase = NewUseCase(links.Object, notification.Object);

        await useCase.ExecuteAsync(5, 10, AttendanceReminderUseCase.ReminderType2h, CancellationToken.None);

        notification.Verify(
            x => x.NotifyMessageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<int?>()),
            Times.Never);
        links.Verify(
            x => x.TryMarkReminderSentAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_Skips_When24hReminderAlreadySent()
    {
        var link = TestLink(sent24hAtUtc: DateTimeOffset.UtcNow);
        var links = new Mock<IBookingCancellationLinkRepository>();
        links.Setup(x => x.GetByMessageAsync(5, 10, It.IsAny<CancellationToken>())).ReturnsAsync(link);
        var notification = new Mock<INotificationSender>(MockBehavior.Strict);
        var useCase = NewUseCase(links.Object, notification.Object);

        await useCase.ExecuteAsync(5, 10, AttendanceReminderUseCase.ReminderType24h, CancellationToken.None);

        notification.Verify(
            x => x.NotifyMessageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<int?>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotMarkSent_WhenDeliveryFails_SoHangfireRetries()
    {
        var link = TestLink();
        var links = new Mock<IBookingCancellationLinkRepository>();
        links.Setup(x => x.GetByMessageAsync(5, 10, It.IsAny<CancellationToken>())).ReturnsAsync(link);
        var notification = new Mock<INotificationSender>();
        notification.Setup(x => x.NotifyMessageAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<int?>()))
            .ThrowsAsync(new InvalidOperationException("telegram down"));
        var useCase = NewUseCase(links.Object, notification.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => useCase.ExecuteAsync(5, 10, AttendanceReminderUseCase.ReminderType24h, CancellationToken.None));

        links.Verify(
            x => x.TryMarkReminderSentAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesCancellation_FromWeather_WithoutSending()
    {
        var link = TestLink();
        var links = new Mock<IBookingCancellationLinkRepository>();
        links.Setup(x => x.GetByMessageAsync(5, 10, It.IsAny<CancellationToken>())).ReturnsAsync(link);
        var notification = new Mock<INotificationSender>(MockBehavior.Strict);
        var weather = new Mock<IWeatherForecastProvider>();
        weather.Setup(x => x.GetForecastAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var useCase = NewUseCase(links.Object, notification.Object, weather.Object);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => useCase.ExecuteAsync(5, 10, AttendanceReminderUseCase.ReminderType24h, CancellationToken.None));

        notification.Verify(
            x => x.NotifyMessageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<int?>()),
            Times.Never);
        links.Verify(
            x => x.TryMarkReminderSentAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(-3.4, true, "-3°C")]
    [InlineData(0.4, false, "0°C")]
    [InlineData(-0.4, false, "0°C")]
    public async Task ExecuteAsync_FormatsNegativeAndZeroTemperatures(double tempC, bool rain, string expectedTemp)
    {
        var link = TestLink();
        var links = new Mock<IBookingCancellationLinkRepository>();
        links.Setup(x => x.GetByMessageAsync(5, 10, It.IsAny<CancellationToken>())).ReturnsAsync(link);
        links.Setup(x => x.TryMarkReminderSentAsync(5, 10, AttendanceReminderUseCase.ReminderType2h, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        string? sent = null;
        var notification = new Mock<INotificationSender>();
        notification.Setup(x => x.NotifyMessageAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<int?>()))
            .Callback<string, CancellationToken, int?>((msg, _, _) => sent = msg)
            .Returns(Task.CompletedTask);
        var weather = new Mock<IWeatherForecastProvider>();
        weather.Setup(x => x.GetForecastAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WeatherForecast(tempC, 80, rain ? 2.5 : 0, rain));
        var useCase = NewUseCase(links.Object, notification.Object, weather.Object);

        await useCase.ExecuteAsync(5, 10, AttendanceReminderUseCase.ReminderType2h, CancellationToken.None);

        Assert.NotNull(sent);
        Assert.Contains(expectedTemp, sent);
        if (expectedTemp == "0°C")
        {
            // "-0°C" and "+0°C" both contain the substring "0°C": pin the exact zero format.
            Assert.DoesNotContain("-0°C", sent);
            Assert.DoesNotContain("+0°C", sent);
        }
    }

    private static BookingCancellationLink TestLink(
        DateTimeOffset? cancelledAtUtc = null,
        DateTimeOffset? sent24hAtUtc = null)
    {
        var user = new BookingUserConfig(1, "u", "p", "r", "v", "vu", DayOfWeek.Monday, 10);
        return new BookingCancellationLink(
            user,
            new BookingSlot(new DateTimeOffset(2030, 6, 15, 10, 0, 0, TimeSpan.Zero)),
            5,
            10,
            "skedda-1",
            DateTimeOffset.UtcNow,
            cancelledAtUtc,
            sent24hAtUtc,
            null);
    }

    private static AttendanceReminderUseCase NewUseCase(
        IBookingCancellationLinkRepository? links = null,
        INotificationSender? notification = null,
        IWeatherForecastProvider? weather = null)
    {
        var linkRepo = links ?? Mock.Of<IBookingCancellationLinkRepository>();
        INotificationSender sender = notification ?? Mock.Of<INotificationSender>();
        IWeatherForecastProvider forecast;
        if (weather is not null)
        {
            forecast = weather;
        }
        else
        {
            var weatherMock = new Mock<IWeatherForecastProvider>();
            weatherMock.Setup(x => x.GetForecastAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((WeatherForecast?)null);
            forecast = weatherMock.Object;
        }
        return new AttendanceReminderUseCase(
            linkRepo, sender, forecast, NullLogger<AttendanceReminderUseCase>.Instance);
    }
}
