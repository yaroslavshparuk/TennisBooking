namespace TennisBooking.Application.Abstractions;

public interface IBookingDeduplicationStore
{
    bool TryBegin(string key);
    void Release(string key);
}
