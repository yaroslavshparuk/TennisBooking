using FluentAssertions;
using TennisBooking.DAL.Models;
using TennisBooking.Tests.Helpers;

namespace TennisBooking.Tests.DAL;

public class ApplicationDbContextTests
{
    [Fact]
    public async Task UserConfigs_CanAddAndRetrieve()
    {
        var db = TestDbContextFactory.Create();

        db.UserConfigs.Add(new UserConfig
        {
            Username = "test@example.com",
            Password = "pass",
            ResourceId = "r1",
            Venue = "v1",
            VenueUser = "vu1",
            DayOfWeek = DayOfWeek.Friday,
            Hour = 10
        });
        await db.SaveChangesAsync();

        var configs = db.UserConfigs.ToList();
        configs.Should().HaveCount(1);
        configs[0].Username.Should().Be("test@example.com");
        configs[0].DayOfWeek.Should().Be(DayOfWeek.Friday);
    }

    [Fact]
    public async Task TelegramConfigs_CanAddAndRetrieve()
    {
        var db = TestDbContextFactory.Create();

        db.TelegramConfigs.Add(new TelegramConfig
        {
            BotToken = "123:ABC",
            ChatId = 555
        });
        await db.SaveChangesAsync();

        var configs = db.TelegramConfigs.ToList();
        configs.Should().HaveCount(1);
        configs[0].BotToken.Should().Be("123:ABC");
        configs[0].ChatId.Should().Be(555);
    }

    [Fact]
    public async Task MultipleUserConfigs_CanCoexist()
    {
        var db = TestDbContextFactory.Create();

        db.UserConfigs.AddRange(
            new UserConfig { Username = "u1", Password = "p1", ResourceId = "r1", Venue = "v1", VenueUser = "vu1", Hour = 10 },
            new UserConfig { Username = "u2", Password = "p2", ResourceId = "r2", Venue = "v2", VenueUser = "vu2", Hour = 14 }
        );
        await db.SaveChangesAsync();

        db.UserConfigs.Count().Should().Be(2);
    }
}
