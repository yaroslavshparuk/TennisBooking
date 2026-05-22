using Microsoft.EntityFrameworkCore;
using TennisBooking.Application.Abstractions;
using TennisBooking.DAL;
using TennisBooking.DAL.Models;

namespace TennisBooking.Infrastructure.Persistence;

public sealed class TelegramPollingStateRepository : ITelegramPollingStateRepository
{
    private const int SingletonId = 1;
    private readonly ApplicationDbContext _db;

    public TelegramPollingStateRepository(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<long?> GetLastProcessedUpdateIdAsync(CancellationToken cancellationToken)
    {
        var state = await _db.TelegramPollingStates.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == SingletonId, cancellationToken);
        return state?.LastProcessedUpdateId;
    }

    public async Task SaveLastProcessedUpdateIdAsync(long updateId, CancellationToken cancellationToken)
    {
        var state = await _db.TelegramPollingStates.FirstOrDefaultAsync(x => x.Id == SingletonId, cancellationToken);
        if (state is null)
        {
            _db.TelegramPollingStates.Add(new TelegramPollingStateEntity
            {
                Id = SingletonId,
                LastProcessedUpdateId = updateId
            });
        }
        else
        {
            state.LastProcessedUpdateId = updateId;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }
}
