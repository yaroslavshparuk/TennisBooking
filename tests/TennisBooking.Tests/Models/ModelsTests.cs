using FluentAssertions;
using TennisBooking.DAL.Models;
using TennisBooking.Models;
using TennisBooking.Options;
using TennisBooking.Services;

namespace TennisBooking.Tests.Models;

public class ModelsTests
{
    [Fact]
    public void BookingInfo_Properties_SetAndGetCorrectly()
    {
        var userConfig = new UserConfig { Id = 1, Username = "test" };
        var body = new { key = "value" };
        var startTime = DateTimeOffset.UtcNow;

        var bookingInfo = new BookingInfo
        {
            UserConfig = userConfig,
            Body = body,
            RequestVerificationToken = "token",
            CsrfCookie = "csrf",
            ApplicationCookie = "app",
            StartTime = startTime
        };

        bookingInfo.UserConfig.Should().Be(userConfig);
        bookingInfo.Body.Should().Be(body);
        bookingInfo.RequestVerificationToken.Should().Be("token");
        bookingInfo.CsrfCookie.Should().Be("csrf");
        bookingInfo.ApplicationCookie.Should().Be("app");
        bookingInfo.StartTime.Should().Be(startTime);
    }

    [Fact]
    public void UserConfig_Properties_SetAndGetCorrectly()
    {
        var config = new UserConfig
        {
            Id = 42,
            Username = "user@test.com",
            Password = "pass123",
            ResourceId = "court-1",
            Venue = "venue-1",
            VenueUser = "venue-user-1",
            DayOfWeek = DayOfWeek.Wednesday,
            Hour = 14
        };

        config.Id.Should().Be(42);
        config.Username.Should().Be("user@test.com");
        config.Password.Should().Be("pass123");
        config.ResourceId.Should().Be("court-1");
        config.Venue.Should().Be("venue-1");
        config.VenueUser.Should().Be("venue-user-1");
        config.DayOfWeek.Should().Be(DayOfWeek.Wednesday);
        config.Hour.Should().Be(14);
    }

    [Fact]
    public void TelegramConfig_Properties_SetAndGetCorrectly()
    {
        var config = new TelegramConfig
        {
            Id = 1,
            BotToken = "123:ABC",
            ChatId = 99887766
        };

        config.Id.Should().Be(1);
        config.BotToken.Should().Be("123:ABC");
        config.ChatId.Should().Be(99887766);
    }

    [Fact]
    public void SkeddaOptions_Properties_SetAndGetCorrectly()
    {
        var options = new SkeddaOptions
        {
            ApiBaseUrl = "https://skedda.example.com"
        };

        options.ApiBaseUrl.Should().Be("https://skedda.example.com");
    }

    [Fact]
    public void SkeddaSession_Properties_SetAndGetCorrectly()
    {
        var session = new SkeddaSession
        {
            RequestVerificationToken = "token-123",
            CsrfCookie = "csrf-456",
            ApplicationCookie = "app-789"
        };

        session.RequestVerificationToken.Should().Be("token-123");
        session.CsrfCookie.Should().Be("csrf-456");
        session.ApplicationCookie.Should().Be("app-789");
    }

    [Fact]
    public void SkeddaSession_DefaultValues_AreEmptyStrings()
    {
        var session = new SkeddaSession();

        session.RequestVerificationToken.Should().BeEmpty();
        session.CsrfCookie.Should().BeEmpty();
        session.ApplicationCookie.Should().BeEmpty();
    }
}
