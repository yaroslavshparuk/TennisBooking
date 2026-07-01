using TennisBooking.Domain.Booking;

namespace TennisBooking.Application.Booking;

public sealed record PreparedBooking(
    BookingUserConfig UserConfig,
    BookingSlot Slot,
    // Pre-serialized JSON request body and pre-built Cookie header, computed during
    // PrepareBookingAsync so nothing but the network send happens at the target instant.
    // Safe to pre-compute because nothing mutates UserConfig/Slot after preparation.
    string BodyJson,
    string CookieHeader,
    string RequestVerificationToken,
    string CsrfCookie,
    string ApplicationCookie);
