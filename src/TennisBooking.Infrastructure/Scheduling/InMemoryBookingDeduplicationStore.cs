using System.Collections.Concurrent;
using TennisBooking.Application.Abstractions;

namespace TennisBooking.Infrastructure.Scheduling;

public sealed class InMemoryBookingDeduplicationStore : IBookingDeduplicationStore
{
    private readonly ConcurrentDictionary<string, byte> _bookingKeys = new();

    public bool TryBegin(string key) => _bookingKeys.TryAdd(key, 0);

    public void Release(string key) => _bookingKeys.TryRemove(key, out _);
}
