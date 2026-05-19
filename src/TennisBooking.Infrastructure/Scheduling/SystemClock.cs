using TennisBooking.Application.Abstractions;

namespace TennisBooking.Infrastructure.Scheduling;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
