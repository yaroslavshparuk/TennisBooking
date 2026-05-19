using TennisBooking.Application.Booking;
using TennisBooking.Domain.Booking;

namespace TennisBooking.Application.Abstractions;

public interface ISkeddaClient
{
    Task<PreparedBooking> PrepareBookingAsync(BookingUserConfig userConfig, BookingSlot slot, CancellationToken cancellationToken);
    Task BookAsync(PreparedBooking booking, CancellationToken cancellationToken);
}
