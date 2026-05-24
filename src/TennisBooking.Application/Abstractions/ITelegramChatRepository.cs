namespace TennisBooking.Application.Abstractions;

public interface ITelegramChatRepository
{
    Task<IReadOnlyList<TelegramChat>> GetAllAsync(CancellationToken cancellationToken);
    Task<TelegramChat?> GetActiveAsync(CancellationToken cancellationToken);
    Task<TelegramChat?> SetActiveAsync(int id, CancellationToken cancellationToken);
}
