using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Moq;
using TennisBooking.DAL.Models;
using TennisBooking.HealthChecks;
using TennisBooking.Services;
using TennisBooking.Tests.Helpers;

namespace TennisBooking.Tests.HealthChecks;

public class PreparationHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_PreparationSucceeds_ReturnsHealthy()
    {
        var db = TestDbContextFactory.Create();
        db.UserConfigs.Add(new UserConfig
        {
            Id = 1,
            Username = "test@example.com",
            Password = "pass",
            ResourceId = "r1",
            Venue = "v1",
            VenueUser = "vu1",
            DayOfWeek = DayOfWeek.Monday,
            Hour = 18
        });
        await db.SaveChangesAsync();

        var bookingServiceMock = new Mock<BookingService>(
            new Mock<ILogger<BookingService>>().Object,
            Microsoft.Extensions.Options.Options.Create(new TennisBooking.Options.SkeddaOptions { ApiBaseUrl = "https://fake.example.com" }),
            CreateMockTelegramService(),
            new Mock<Hangfire.IBackgroundJobClient>().Object,
            new Mock<ISkeddaApiClient>().Object)
        { CallBase = false };

        bookingServiceMock
            .Setup(x => x.Preparation(It.IsAny<UserConfig>(), false, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var logger = new Mock<ILogger<PreparationHealthCheck>>();
        var healthCheck = new PreparationHealthCheck(bookingServiceMock.Object, db, logger.Object);

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task CheckHealthAsync_PreparationThrows_ReturnsUnhealthy()
    {
        var db = TestDbContextFactory.Create();
        db.UserConfigs.Add(new UserConfig
        {
            Id = 1,
            Username = "test@example.com",
            Password = "pass",
            ResourceId = "r1",
            Venue = "v1",
            VenueUser = "vu1",
            DayOfWeek = DayOfWeek.Monday,
            Hour = 18
        });
        await db.SaveChangesAsync();

        var bookingServiceMock = new Mock<BookingService>(
            new Mock<ILogger<BookingService>>().Object,
            Microsoft.Extensions.Options.Options.Create(new TennisBooking.Options.SkeddaOptions { ApiBaseUrl = "https://fake.example.com" }),
            CreateMockTelegramService(),
            new Mock<Hangfire.IBackgroundJobClient>().Object,
            new Mock<ISkeddaApiClient>().Object)
        { CallBase = false };

        bookingServiceMock
            .Setup(x => x.Preparation(It.IsAny<UserConfig>(), false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API down"));

        var logger = new Mock<ILogger<PreparationHealthCheck>>();
        var healthCheck = new PreparationHealthCheck(bookingServiceMock.Object, db, logger.Object);

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task CheckHealthAsync_NoUserConfigs_CallsPreparationWithNull()
    {
        var db = TestDbContextFactory.Create();
        // No UserConfigs in DB

        var bookingServiceMock = new Mock<BookingService>(
            new Mock<ILogger<BookingService>>().Object,
            Microsoft.Extensions.Options.Options.Create(new TennisBooking.Options.SkeddaOptions { ApiBaseUrl = "https://fake.example.com" }),
            CreateMockTelegramService(),
            new Mock<Hangfire.IBackgroundJobClient>().Object,
            new Mock<ISkeddaApiClient>().Object)
        { CallBase = false };

        bookingServiceMock
            .Setup(x => x.Preparation(null!, false, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var logger = new Mock<ILogger<PreparationHealthCheck>>();
        var healthCheck = new PreparationHealthCheck(bookingServiceMock.Object, db, logger.Object);

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);

        bookingServiceMock.Verify(
            x => x.Preparation(null!, false, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static TelegramService CreateMockTelegramService()
    {
        var httpClient = new HttpClient();
        var db = TestDbContextFactory.Create();
        var logger = new Mock<ILogger<TelegramService>>();
        var mock = new Mock<TelegramService>(httpClient, db, logger.Object) { CallBase = false };
        return mock.Object;
    }
}
