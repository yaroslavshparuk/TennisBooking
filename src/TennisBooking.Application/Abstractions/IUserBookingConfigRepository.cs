using TennisBooking.Domain.Booking;

namespace TennisBooking.Application.Abstractions;

public interface IUserBookingConfigRepository
{
    Task<IReadOnlyList<BookingUserConfig>> GetAllAsync(CancellationToken cancellationToken);
    Task<BookingUserConfig?> GetByIdAsync(int id, CancellationToken cancellationToken);
    Task<BookingUserConfig?> FirstOrDefaultAsync(CancellationToken cancellationToken);
    Task<BookingUserConfig?> UpdateScheduleAsync(int id, DayOfWeek dayOfWeek, int hour, CancellationToken cancellationToken);
}
