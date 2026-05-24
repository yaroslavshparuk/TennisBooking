using TennisBooking.Domain.Booking;

namespace TennisBooking.Models;

public sealed record SettingsIndexViewModel(
    IReadOnlyList<UserConfigScheduleViewModel> UserConfigs,
    TelegramChatsViewModel TelegramChats);

public sealed record TelegramChatsViewModel(
    IReadOnlyList<TelegramChatOptionViewModel> Chats,
    string? Message = null,
    bool IsError = false);

public sealed record TelegramChatOptionViewModel(
    int Id,
    string Name,
    long ChatId,
    bool IsActive)
{
    public static TelegramChatOptionViewModel FromDomain(TennisBooking.Application.Abstractions.TelegramChat chat)
        => new(chat.Id, chat.Name, chat.ChatId, chat.IsActive);
}

public sealed record UserConfigScheduleViewModel(
    int Id,
    string Username,
    DayOfWeek DayOfWeek,
    int Hour,
    string? Message = null,
    bool IsError = false)
{
    public static UserConfigScheduleViewModel FromDomain(
        BookingUserConfig config,
        string? message = null,
        bool isError = false)
        => new(
            config.Id,
            config.Username,
            config.DayOfWeek,
            config.Hour,
            message,
            isError);
}
