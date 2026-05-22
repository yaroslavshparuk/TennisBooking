using Microsoft.EntityFrameworkCore;
using TennisBooking.Application.Abstractions;
using TennisBooking.DAL;
using TennisBooking.DAL.Models;
using TennisBooking.Domain.Booking;

namespace TennisBooking.Infrastructure.Persistence;

public sealed class BookingCancellationLinkRepository : IBookingCancellationLinkRepository
{
    private readonly ApplicationDbContext _db;

    public BookingCancellationLinkRepository(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task SaveAsync(
        BookingUserConfig userConfig,
        BookingSlot slot,
        long chatId,
        int telegramMessageId,
        string skeddaBookingId,
        CancellationToken cancellationToken)
    {
        var entity = new BookingCancellationLinkEntity
        {
            UserConfigId = userConfig.Id,
            Username = userConfig.Username,
            Password = userConfig.Password,
            ResourceId = userConfig.ResourceId,
            Venue = userConfig.Venue,
            VenueUser = userConfig.VenueUser,
            DayOfWeek = userConfig.DayOfWeek,
            Hour = userConfig.Hour,
            SlotStartUtc = slot.StartTime.ToUniversalTime(),
            SlotEndUtc = slot.EndTime.ToUniversalTime(),
            ChatId = chatId,
            TelegramMessageId = telegramMessageId,
            SkeddaBookingId = skeddaBookingId,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        _db.BookingCancellationLinks.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<BookingCancellationLink?> GetByReplyAsync(long chatId, int repliedTelegramMessageId, CancellationToken cancellationToken)
    {
        var entity = await _db.BookingCancellationLinks
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.ChatId == chatId && x.TelegramMessageId == repliedTelegramMessageId,
                cancellationToken);

        if (entity is null)
            return null;

        var userConfig = new BookingUserConfig(
            entity.UserConfigId,
            entity.Username,
            entity.Password,
            entity.ResourceId,
            entity.Venue,
            entity.VenueUser,
            entity.DayOfWeek,
            entity.Hour);

        return new BookingCancellationLink(
            userConfig,
            new BookingSlot(entity.SlotStartUtc),
            entity.ChatId,
            entity.TelegramMessageId,
            entity.SkeddaBookingId,
            entity.CreatedAtUtc,
            entity.CancelledAtUtc);
    }

    public async Task<bool> TryMarkCancelledAsync(long chatId, int repliedTelegramMessageId, int cancelRequestMessageId, CancellationToken cancellationToken)
    {
        var entity = await _db.BookingCancellationLinks.FirstOrDefaultAsync(
            x => x.ChatId == chatId && x.TelegramMessageId == repliedTelegramMessageId,
            cancellationToken);

        if (entity is null || entity.CancelledAtUtc.HasValue)
            return false;

        entity.CancelledAtUtc = DateTimeOffset.UtcNow;
        entity.CancelRequestMessageId = cancelRequestMessageId;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
