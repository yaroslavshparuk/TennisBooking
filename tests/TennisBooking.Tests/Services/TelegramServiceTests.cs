using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using TennisBooking.DAL.Models;
using TennisBooking.Services;
using TennisBooking.Tests.Helpers;

namespace TennisBooking.Tests.Services;

public class TelegramServiceTests
{
    [Fact]
    public async Task NotifyAsync_ValidConfig_SendsMessageToTelegramApi()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, """{"ok": true}""");
        var httpClient = new HttpClient(handler);

        var db = TestDbContextFactory.Create();
        db.TelegramConfigs.Add(new TelegramConfig
        {
            Id = 1,
            BotToken = "123456:ABC-DEF",
            ChatId = 987654321
        });
        await db.SaveChangesAsync();

        var logger = new Mock<ILogger<TelegramService>>();
        var service = new TelegramService(httpClient, db, logger.Object);

        await service.NotifyAsync("Test message");

        handler.Requests.Should().HaveCount(1);
        var request = handler.Requests[0];
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri!.ToString().Should().Contain("123456:ABC-DEF/sendMessage");

        var body = await request.Content!.ReadAsStringAsync();
        body.Should().Contain("987654321");
        body.Should().Contain("Test message");
        body.Should().Contain("MarkdownV2");
    }

    [Fact]
    public async Task NotifyAsync_NoConfig_LogsErrorAndDoesNotThrow()
    {
        var handler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(handler);

        var db = TestDbContextFactory.Create();
        // No TelegramConfig in DB

        var logger = new Mock<ILogger<TelegramService>>();
        var service = new TelegramService(httpClient, db, logger.Object);

        // Should not throw
        await service.NotifyAsync("Test message");

        logger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("Failed to send Telegram notification")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);

        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task NotifyAsync_ApiReturnsError_LogsErrorAndDoesNotThrow()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.BadRequest, """{"ok": false}""");
        var httpClient = new HttpClient(handler);

        var db = TestDbContextFactory.Create();
        db.TelegramConfigs.Add(new TelegramConfig
        {
            Id = 1,
            BotToken = "token",
            ChatId = 123
        });
        await db.SaveChangesAsync();

        var logger = new Mock<ILogger<TelegramService>>();
        var service = new TelegramService(httpClient, db, logger.Object);

        await service.NotifyAsync("Fail message");

        logger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("Failed to send Telegram notification")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
