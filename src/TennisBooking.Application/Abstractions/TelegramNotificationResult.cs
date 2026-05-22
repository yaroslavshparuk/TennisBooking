namespace TennisBooking.Application.Abstractions;

public sealed record TelegramNotificationResult(long ChatId, int MessageId);
