using Microsoft.EntityFrameworkCore;
using TennisBooking.Application.Abstractions;
using TennisBooking.Application.Booking;
using TennisBooking.DAL;
using TennisBooking.DAL.Models;
using TennisBooking.Domain.Booking;
using TennisBooking.Infrastructure.Persistence;
using Xunit;

namespace TennisBooking.Tests.Integration;

/// <summary>
/// Repository integration tests over EF Core InMemory (no containers, no network).
/// Covers the CRUD/edge branches of the four repositories that the Testcontainers
/// Postgres suite cannot reach in this environment.
/// NOTE: InMemory ignores relational constraints (unique indexes, partial filters),
/// so duplicate-key branches are deliberately NOT asserted here — they need real Postgres.
/// </summary>
public sealed class RepositoryIntegrationTests
{
    private static ApplicationDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private static UserConfig NewConfig(string username, DayOfWeek day = DayOfWeek.Monday, int hour = 10) => new()
    {
        Username = username,
        Password = "pw-" + username,
        ResourceId = "res-" + username,
        Venue = "venue",
        VenueUser = "venue-user",
        DayOfWeek = day,
        Hour = hour
    };

    // ---- UserBookingConfigRepository ----

    [Fact]
    public async Task UserConfigs_GetAll_MapsEveryField()
    {
        await using var db = NewDb();
        db.UserConfigs.Add(NewConfig("u1", DayOfWeek.Wednesday, 18));
        db.UserConfigs.Add(NewConfig("u2", DayOfWeek.Friday, 7));
        await db.SaveChangesAsync();
        var repo = new UserBookingConfigRepository(db);

        var all = await repo.GetAllAsync(CancellationToken.None);

        Assert.Equal(2, all.Count);
        var first = Assert.Single(all, x => x.Username == "u1");
        Assert.Equal("pw-u1", first.Password);
        Assert.Equal("res-u1", first.ResourceId);
        Assert.Equal("venue", first.Venue);
        Assert.Equal("venue-user", first.VenueUser);
        Assert.Equal(DayOfWeek.Wednesday, first.DayOfWeek);
        Assert.Equal(18, first.Hour);
    }

    [Fact]
    public async Task UserConfigs_GetById_Missing_ReturnsNull()
    {
        await using var db = NewDb();
        var repo = new UserBookingConfigRepository(db);

        Assert.Null(await repo.GetByIdAsync(4242, CancellationToken.None));
    }

    [Fact]
    public async Task UserConfigs_FirstOrDefault_ReturnsLowestId_ThenNullWhenEmpty()
    {
        await using var db = NewDb();
        var repo = new UserBookingConfigRepository(db);
        Assert.Null(await repo.FirstOrDefaultAsync(CancellationToken.None));

        db.UserConfigs.Add(NewConfig("b"));
        db.UserConfigs.Add(NewConfig("a"));
        await db.SaveChangesAsync();

        var first = await repo.FirstOrDefaultAsync(CancellationToken.None);
        Assert.NotNull(first);
        Assert.Equal("b", first.Username);
    }

    [Fact]
    public async Task UserConfigs_UpdateSchedule_PersistsDayAndHour()
    {
        await using var db = NewDb();
        db.UserConfigs.Add(NewConfig("upd", DayOfWeek.Monday, 10));
        await db.SaveChangesAsync();
        var id = await db.UserConfigs.Select(x => x.Id).SingleAsync();
        var repo = new UserBookingConfigRepository(db);

        var updated = await repo.UpdateScheduleAsync(id, DayOfWeek.Sunday, 21, CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal(DayOfWeek.Sunday, updated.DayOfWeek);
        Assert.Equal(21, updated.Hour);
        var reread = await db.UserConfigs.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal(DayOfWeek.Sunday, reread.DayOfWeek);
        Assert.Equal(21, reread.Hour);
    }

    [Fact]
    public async Task UserConfigs_UpdateSchedule_Missing_ReturnsNull()
    {
        await using var db = NewDb();
        var repo = new UserBookingConfigRepository(db);

        Assert.Null(await repo.UpdateScheduleAsync(9999, DayOfWeek.Monday, 10, CancellationToken.None));
    }

    // ---- BookingCancellationLinkRepository ----

    private static BookingUserConfig Domain(int id = 7) =>
        new(id, "user", "pw", "res", "venue", "venue-user", DayOfWeek.Tuesday, 11);

    [Fact]
    public async Task Links_SaveAndGetByReply_RoundtripsAllFields()
    {
        await using var db = NewDb();
        var repo = new BookingCancellationLinkRepository(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<BookingCancellationLinkRepository>.Instance);
        var slot = new BookingSlot(new DateTimeOffset(2030, 3, 4, 10, 0, 0, TimeSpan.Zero));

        await repo.SaveAsync(Domain(), slot, chatId: 111, telegramMessageId: 222, skeddaBookingId: "sk-1", CancellationToken.None);

        var link = await repo.GetByReplyAsync(111, 222, CancellationToken.None);
        Assert.NotNull(link);
        Assert.Equal(111, link.ChatId);
        Assert.Equal(222, link.TelegramMessageId);
        Assert.Equal("sk-1", link.SkeddaBookingId);
        Assert.Equal(slot.StartTime, link.Slot.StartTime);
        Assert.Equal("user", link.UserConfig.Username);
        Assert.Equal(DayOfWeek.Tuesday, link.UserConfig.DayOfWeek);
        Assert.Null(link.CancelledAtUtc);

        var byMessage = await repo.GetByMessageAsync(111, 222, CancellationToken.None);
        Assert.NotNull(byMessage);
        Assert.Equal("sk-1", byMessage.SkeddaBookingId);
    }

    [Fact]
    public async Task Links_GetByReply_And_GetByMessage_Missing_ReturnNull()
    {
        await using var db = NewDb();
        var repo = new BookingCancellationLinkRepository(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<BookingCancellationLinkRepository>.Instance);

        Assert.Null(await repo.GetByReplyAsync(1, 2, CancellationToken.None));
        Assert.Null(await repo.GetByMessageAsync(1, 2, CancellationToken.None));
    }

    [Fact]
    public async Task Links_TryMarkCancelled_FirstTrue_SecondFalse_SetsTimestamp()
    {
        await using var db = NewDb();
        var repo = new BookingCancellationLinkRepository(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<BookingCancellationLinkRepository>.Instance);
        await repo.SaveAsync(Domain(), new BookingSlot(new DateTimeOffset(2030, 3, 4, 10, 0, 0, TimeSpan.Zero)), 5, 6, "sk-9", CancellationToken.None);

        Assert.True(await repo.TryMarkCancelledAsync(5, 6, cancelRequestMessageId: 77, CancellationToken.None));
        Assert.False(await repo.TryMarkCancelledAsync(5, 6, cancelRequestMessageId: 78, CancellationToken.None));

        var link = await repo.GetByReplyAsync(5, 6, CancellationToken.None);
        Assert.NotNull(link);
        Assert.NotNull(link.CancelledAtUtc);
    }

    [Fact]
    public async Task Links_TryMarkCancelled_Missing_ReturnsFalse()
    {
        await using var db = NewDb();
        var repo = new BookingCancellationLinkRepository(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<BookingCancellationLinkRepository>.Instance);

        Assert.False(await repo.TryMarkCancelledAsync(9, 9, 1, CancellationToken.None));
    }

    [Fact]
    public async Task Links_SaveReminderJobId_PersistsBothTypes_RejectsUnknown()
    {
        await using var db = NewDb();
        var repo = new BookingCancellationLinkRepository(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<BookingCancellationLinkRepository>.Instance);
        await repo.SaveAsync(Domain(), new BookingSlot(new DateTimeOffset(2030, 3, 4, 10, 0, 0, TimeSpan.Zero)), 5, 6, "sk-9", CancellationToken.None);

        Assert.True(await repo.SaveReminderJobIdAsync(5, 6, AttendanceReminderUseCase.ReminderType24h, "job-24", CancellationToken.None));
        Assert.True(await repo.SaveReminderJobIdAsync(5, 6, AttendanceReminderUseCase.ReminderType2h, "job-2", CancellationToken.None));

        var link = await repo.GetByMessageAsync(5, 6, CancellationToken.None);
        Assert.NotNull(link);
        Assert.Equal("job-24", link.AttendanceReminder24hJobId);
        Assert.Equal("job-2", link.AttendanceReminder2hJobId);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repo.SaveReminderJobIdAsync(5, 6, "3h", "job-x", CancellationToken.None));
    }

    [Fact]
    public async Task Links_SaveReminderJobId_Missing_ReturnsFalse()
    {
        await using var db = NewDb();
        var repo = new BookingCancellationLinkRepository(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<BookingCancellationLinkRepository>.Instance);

        Assert.False(await repo.SaveReminderJobIdAsync(1, 2, AttendanceReminderUseCase.ReminderType24h, "j", CancellationToken.None));
    }

    [Fact]
    public async Task Links_TryMarkReminderSent_OnceOnly_PerType_BlockedAfterCancel()
    {
        await using var db = NewDb();
        var repo = new BookingCancellationLinkRepository(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<BookingCancellationLinkRepository>.Instance);
        await repo.SaveAsync(Domain(), new BookingSlot(new DateTimeOffset(2030, 3, 4, 10, 0, 0, TimeSpan.Zero)), 5, 6, "sk-9", CancellationToken.None);

        Assert.True(await repo.TryMarkReminderSentAsync(5, 6, AttendanceReminderUseCase.ReminderType24h, CancellationToken.None));
        Assert.False(await repo.TryMarkReminderSentAsync(5, 6, AttendanceReminderUseCase.ReminderType24h, CancellationToken.None));
        Assert.True(await repo.TryMarkReminderSentAsync(5, 6, AttendanceReminderUseCase.ReminderType2h, CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repo.TryMarkReminderSentAsync(5, 6, "nope", CancellationToken.None));

        Assert.True(await repo.TryMarkCancelledAsync(5, 6, 1, CancellationToken.None));
        Assert.False(await repo.TryMarkReminderSentAsync(5, 6, AttendanceReminderUseCase.ReminderType2h, CancellationToken.None));
    }

    // ---- TelegramChatRepository ----

    [Fact]
    public async Task Chats_GetAll_OrdersActiveFirst_ThenByName()
    {
        await using var db = NewDb();
        db.TelegramChats.Add(new TelegramChatEntity { Name = "b-inactive", ChatId = 2, IsActive = false });
        db.TelegramChats.Add(new TelegramChatEntity { Name = "a-active", ChatId = 1, IsActive = true });
        db.TelegramChats.Add(new TelegramChatEntity { Name = "a-inactive", ChatId = 3, IsActive = false });
        await db.SaveChangesAsync();
        var repo = new TelegramChatRepository(db);

        var all = await repo.GetAllAsync(CancellationToken.None);

        Assert.Equal(["a-active", "a-inactive", "b-inactive"], all.Select(x => x.Name).ToArray());
    }

    [Fact]
    public async Task Chats_GetActive_None_ReturnsNull()
    {
        await using var db = NewDb();
        var repo = new TelegramChatRepository(db);

        Assert.Null(await repo.GetActiveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Chats_SetActive_SwitchesActiveChat_And_MissingReturnsNull()
    {
        await using var db = NewDb();
        db.TelegramChats.Add(new TelegramChatEntity { Name = "one", ChatId = 1, IsActive = true });
        db.TelegramChats.Add(new TelegramChatEntity { Name = "two", ChatId = 2, IsActive = false });
        await db.SaveChangesAsync();
        var ids = await db.TelegramChats.OrderBy(x => x.Id).Select(x => x.Id).ToListAsync();
        var repo = new TelegramChatRepository(db);

        Assert.Null(await repo.SetActiveAsync(424242, CancellationToken.None));

        var active = await repo.SetActiveAsync(ids[1], CancellationToken.None);
        Assert.NotNull(active);
        Assert.Equal(2, active.ChatId);
        Assert.True(active.IsActive);
        Assert.Equal(2, await repo.GetActiveAsync(CancellationToken.None) switch { { } c => c.ChatId, _ => 0 });
        Assert.False(await db.TelegramChats.Where(x => x.Id == ids[0]).Select(x => x.IsActive).SingleAsync());
    }

    // ---- TelegramPollingStateRepository ----

    [Fact]
    public async Task PollingState_InitiallyNull_SaveAndUpdate_Roundtrips()
    {
        await using var db = NewDb();
        var repo = new TelegramPollingStateRepository(db);

        Assert.Null(await repo.GetLastProcessedUpdateIdAsync(CancellationToken.None));

        await repo.SaveLastProcessedUpdateIdAsync(100, CancellationToken.None);
        Assert.Equal(100, await repo.GetLastProcessedUpdateIdAsync(CancellationToken.None));

        await repo.SaveLastProcessedUpdateIdAsync(101, CancellationToken.None);
        Assert.Equal(101, await repo.GetLastProcessedUpdateIdAsync(CancellationToken.None));
        Assert.Equal(1, await db.TelegramPollingStates.CountAsync());
    }
}
