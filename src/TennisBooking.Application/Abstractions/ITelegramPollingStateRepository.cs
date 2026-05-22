namespace TennisBooking.Application.Abstractions;

public interface ITelegramPollingStateRepository
{
    Task<long?> GetLastProcessedUpdateIdAsync(CancellationToken cancellationToken);
    Task SaveLastProcessedUpdateIdAsync(long updateId, CancellationToken cancellationToken);
}
