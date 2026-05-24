namespace TennisBooking.Application.Abstractions;

public sealed record TelegramChat(int Id, string Name, long ChatId, bool IsActive);
