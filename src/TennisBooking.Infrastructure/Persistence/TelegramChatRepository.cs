using Microsoft.EntityFrameworkCore;
using TennisBooking.Application.Abstractions;
using TennisBooking.DAL;

namespace TennisBooking.Infrastructure.Persistence;

public sealed class TelegramChatRepository : ITelegramChatRepository
{
    private readonly ApplicationDbContext _db;

    public TelegramChatRepository(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<TelegramChat>> GetAllAsync(CancellationToken cancellationToken)
        => await _db.TelegramChats
            .AsNoTracking()
            .OrderByDescending(x => x.IsActive)
            .ThenBy(x => x.Name)
            .Select(x => ToDomain(x))
            .ToListAsync(cancellationToken);

    public async Task<TelegramChat?> GetActiveAsync(CancellationToken cancellationToken)
    {
        var entity = await _db.TelegramChats
            .AsNoTracking()
            .OrderBy(x => x.Id)
            .FirstOrDefaultAsync(x => x.IsActive, cancellationToken);
        return entity is null ? null : ToDomain(entity);
    }

    public async Task<TelegramChat?> SetActiveAsync(int id, CancellationToken cancellationToken)
    {
        var chats = await _db.TelegramChats.ToListAsync(cancellationToken);
        var selected = chats.FirstOrDefault(x => x.Id == id);
        if (selected is null)
            return null;

        foreach (var chat in chats)
            chat.IsActive = false;

        await _db.SaveChangesAsync(cancellationToken);

        selected.IsActive = true;
        await _db.SaveChangesAsync(cancellationToken);
        return ToDomain(selected);
    }

    private static TelegramChat ToDomain(TennisBooking.DAL.Models.TelegramChatEntity entity)
        => new(entity.Id, entity.Name, entity.ChatId, entity.IsActive);
}
