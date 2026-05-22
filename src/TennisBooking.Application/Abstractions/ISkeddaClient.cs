using TennisBooking.Application.Booking;
using TennisBooking.Domain.Booking;

namespace TennisBooking.Application.Abstractions;

public interface ISkeddaClient
{
    Task<PreparedBooking> PrepareBookingAsync(BookingUserConfig userConfig, BookingSlot slot, CancellationToken cancellationToken);
    Task<SkeddaBookingResult> BookAsync(PreparedBooking booking, CancellationToken cancellationToken);
    Task CancelAsync(PreparedBooking booking, string bookingId, CancellationToken cancellationToken);
}
