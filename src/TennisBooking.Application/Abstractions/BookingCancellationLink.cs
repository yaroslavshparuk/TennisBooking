using TennisBooking.Domain.Booking;

namespace TennisBooking.Application.Abstractions;

public sealed record BookingCancellationLink(
    BookingUserConfig UserConfig,
    BookingSlot Slot,
    long ChatId,
    int TelegramMessageId,
    string SkeddaBookingId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CancelledAtUtc);
