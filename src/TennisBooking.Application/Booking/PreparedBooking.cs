using TennisBooking.Domain.Booking;

namespace TennisBooking.Application.Booking;

public sealed record PreparedBooking(
    BookingUserConfig UserConfig,
    BookingSlot Slot,
    object Body,
    string RequestVerificationToken,
    string CsrfCookie,
    string ApplicationCookie);
