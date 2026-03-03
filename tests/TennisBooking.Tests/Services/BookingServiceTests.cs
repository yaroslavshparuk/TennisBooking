using System.Globalization;
using System.Net;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using TennisBooking.DAL.Models;
using TennisBooking.Models;
using TennisBooking.Options;
using TennisBooking.Services;
using TennisBooking.Tests.Helpers;

namespace TennisBooking.Tests.Services;

public class BookingServiceTests
{
    private static readonly UserConfig TestUserConfig = new()
    {
        Id = 1,
        Username = "testuser@example.com",
        Password = "testpassword",
        ResourceId = "court-1",
        Venue = "test-venue",
        VenueUser = "test-venue-user",
        DayOfWeek = DayOfWeek.Monday,
        Hour = 18
    };

    private readonly Mock<ILogger<BookingService>> _loggerMock = new();
    private readonly Mock<IBackgroundJobClient> _backgroundJobClientMock = new();
    private readonly Mock<TelegramService> _telegramMock;
    private readonly Mock<ISkeddaApiClient> _skeddaApiClientMock = new();
    private readonly IOptions<SkeddaOptions> _options;

    public BookingServiceTests()
    {
        _options = Options.Create(new SkeddaOptions { ApiBaseUrl = "https://fake-skedda.example.com" });

        var httpClient = new HttpClient();
        var db = TestDbContextFactory.Create();
        var logger = new Mock<ILogger<TelegramService>>();
        _telegramMock = new Mock<TelegramService>(httpClient, db, logger.Object) { CallBase = false };
        _telegramMock.Setup(x => x.NotifyAsync(It.IsAny<string>())).Returns(Task.CompletedTask);
    }

    private BookingService CreateService()
    {
        return new BookingService(
            _loggerMock.Object,
            _options,
            _telegramMock.Object,
            _backgroundJobClientMock.Object,
            _skeddaApiClientMock.Object);
    }

    // ── Preparation ──

    [Fact]
    public async Task Preparation_NullUserConfig_LogsErrorAndReturns()
    {
        var service = CreateService();

        await service.Preparation(null!, false, CancellationToken.None);

        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("UserConfig is null")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);

        _skeddaApiClientMock.Verify(
            x => x.LoginAndGetSessionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Preparation_ValidConfig_NoSchedule_CallsLoginButDoesNotSchedule()
    {
        _skeddaApiClientMock
            .Setup(x => x.LoginAndGetSessionAsync(TestUserConfig.Username, TestUserConfig.Password, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SkeddaSession
            {
                RequestVerificationToken = "token-123",
                CsrfCookie = "csrf-cookie",
                ApplicationCookie = "app-cookie"
            });

        var service = CreateService();

        await service.Preparation(TestUserConfig, false, CancellationToken.None);

        _skeddaApiClientMock.Verify(
            x => x.LoginAndGetSessionAsync(TestUserConfig.Username, TestUserConfig.Password, It.IsAny<CancellationToken>()),
            Times.Once);

        _backgroundJobClientMock.Verify(
            x => x.Create(It.IsAny<Job>(), It.IsAny<IState>()),
            Times.Never);
    }

    [Fact]
    public async Task Preparation_ValidConfig_WithSchedule_SchedulesBookingJob()
    {
        _skeddaApiClientMock
            .Setup(x => x.LoginAndGetSessionAsync(TestUserConfig.Username, TestUserConfig.Password, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SkeddaSession
            {
                RequestVerificationToken = "token-123",
                CsrfCookie = "csrf-cookie",
                ApplicationCookie = "app-cookie"
            });

        var service = CreateService();

        await service.Preparation(TestUserConfig, true, CancellationToken.None);

        _backgroundJobClientMock.Verify(
            x => x.Create(It.IsAny<Job>(), It.IsAny<ScheduledState>()),
            Times.Once);
    }

    [Fact]
    public async Task Preparation_ApiThrows_LogsErrorAndRethrows()
    {
        _skeddaApiClientMock
            .Setup(x => x.LoginAndGetSessionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var service = CreateService();

        var act = () => service.Preparation(TestUserConfig, false, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("Connection refused");

        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("Failed to book court")),
                It.IsAny<HttpRequestException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    // ── Booking ──

    [Fact]
    public async Task Booking_NullBookingInfo_LogsErrorAndReturns()
    {
        var service = CreateService();

        await service.Booking(null!, CancellationToken.None);

        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("BookingInfo is null")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);

        _skeddaApiClientMock.Verify(
            x => x.BookAsync(It.IsAny<SkeddaSession>(), It.IsAny<object>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Booking_ValidInfo_Success_SendsTelegramNotification()
    {
        _skeddaApiClientMock
            .Setup(x => x.BookAsync(It.IsAny<SkeddaSession>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");
        var startTime = new DateTimeOffset(2026, 3, 10, 18, 0, 0, tz.GetUtcOffset(new DateTime(2026, 3, 10)));

        var bookingInfo = new BookingInfo
        {
            UserConfig = TestUserConfig,
            Body = new { booking = new { title = "test" } },
            RequestVerificationToken = "test-token",
            CsrfCookie = "csrf-cookie-value",
            ApplicationCookie = "app-cookie-value",
            StartTime = startTime
        };

        var service = CreateService();

        await service.Booking(bookingInfo, CancellationToken.None);

        _skeddaApiClientMock.Verify(
            x => x.BookAsync(
                It.Is<SkeddaSession>(s =>
                    s.RequestVerificationToken == "test-token" &&
                    s.CsrfCookie == "csrf-cookie-value" &&
                    s.ApplicationCookie == "app-cookie-value"),
                bookingInfo.Body,
                It.IsAny<CancellationToken>()),
            Times.Once);

        _telegramMock.Verify(x => x.NotifyAsync(It.Is<string>(msg => msg.Contains("🎾") && msg.Contains("18:00"))), Times.Once);

        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("Booked court")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task Booking_ApiThrows_LogsErrorAndRethrows()
    {
        _skeddaApiClientMock
            .Setup(x => x.BookAsync(It.IsAny<SkeddaSession>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Response from Skedda: 403 Forbidden"));

        var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");
        var startTime = new DateTimeOffset(2026, 3, 10, 18, 0, 0, tz.GetUtcOffset(new DateTime(2026, 3, 10)));

        var bookingInfo = new BookingInfo
        {
            UserConfig = TestUserConfig,
            Body = new { booking = new { title = "test" } },
            RequestVerificationToken = "test-token",
            CsrfCookie = "csrf",
            ApplicationCookie = "app",
            StartTime = startTime
        };

        var service = CreateService();

        var act = () => service.Booking(bookingInfo, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();

        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("Failed to book court")),
                It.IsAny<HttpRequestException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);

        _telegramMock.Verify(x => x.NotifyAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Booking_TelegramFormatsMessageCorrectly()
    {
        _skeddaApiClientMock
            .Setup(x => x.BookAsync(It.IsAny<SkeddaSession>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");
        var startTime = new DateTimeOffset(2026, 3, 10, 18, 0, 0, tz.GetUtcOffset(new DateTime(2026, 3, 10)));

        var bookingInfo = new BookingInfo
        {
            UserConfig = TestUserConfig,
            Body = new { },
            RequestVerificationToken = "t",
            CsrfCookie = "c",
            ApplicationCookie = "a",
            StartTime = startTime
        };

        var service = CreateService();
        await service.Booking(bookingInfo, CancellationToken.None);

        var expectedStart = startTime.ToString("HH:mm", new CultureInfo("uk-UA"));
        var expectedEnd = startTime.AddHours(1).ToString("HH:mm", new CultureInfo("uk-UA"));

        _telegramMock.Verify(x => x.NotifyAsync(
            It.Is<string>(msg =>
                msg.Contains("🎾 Забронював тенісний корт в Галактиці") &&
                msg.Contains($"{expectedStart}–{expectedEnd}"))),
            Times.Once);
    }
}
