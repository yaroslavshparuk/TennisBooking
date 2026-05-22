using Microsoft.EntityFrameworkCore;
using TennisBooking.Application.Abstractions;
using TennisBooking.DAL;
using TennisBooking.DAL.Models;
using TennisBooking.Domain.Booking;

namespace TennisBooking.Infrastructure.Persistence;

public sealed class UserBookingConfigRepository : IUserBookingConfigRepository
{
    private readonly ApplicationDbContext _db;

    public UserBookingConfigRepository(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<BookingUserConfig>> GetAllAsync(CancellationToken cancellationToken)
        => await _db.UserConfigs
            .AsNoTracking()
            .Select(x => ToDomain(x))
            .ToListAsync(cancellationToken);

    public async Task<BookingUserConfig?> GetByIdAsync(int id, CancellationToken cancellationToken)
    {
        var entity = await _db.UserConfigs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        return entity is null ? null : ToDomain(entity);
    }

    public async Task<BookingUserConfig?> FirstOrDefaultAsync(CancellationToken cancellationToken)
    {
        var entity = await _db.UserConfigs
            .AsNoTracking()
            .OrderBy(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return entity is null ? null : ToDomain(entity);
    }

    private static BookingUserConfig ToDomain(UserConfig entity)
        => new(
            entity.Id,
            entity.Username,
            entity.Password,
            entity.ResourceId,
            entity.Venue,
            entity.VenueUser,
            entity.DayOfWeek,
            entity.Hour);
}
