using Microsoft.AspNetCore.Mvc;
using TennisBooking.Application.Abstractions;
using TennisBooking.Application.Booking;
using TennisBooking.Domain.Booking;
using TennisBooking.Models;

namespace TennisBooking.Controllers;

[Route("settings")]
public sealed class SettingsController : Controller
{
    private readonly IUserBookingConfigRepository _userConfigs;
    private readonly ITelegramChatRepository _telegramChats;
    private readonly UpdateBookingScheduleUseCase _updateBookingSchedule;

    public SettingsController(
        IUserBookingConfigRepository userConfigs,
        ITelegramChatRepository telegramChats,
        UpdateBookingScheduleUseCase updateBookingSchedule)
    {
        _userConfigs = userConfigs;
        _telegramChats = telegramChats;
        _updateBookingSchedule = updateBookingSchedule;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var configs = await _userConfigs.GetAllAsync(cancellationToken);
        var chats = await BuildTelegramChatsModelAsync(null, false, cancellationToken);
        var model = new SettingsIndexViewModel(
            configs.Select(x => UserConfigScheduleViewModel.FromDomain(x)).ToList(),
            chats);
        return View(model);
    }

    [HttpPost("telegram-chat")]
    public async Task<IActionResult> UpdateTelegramChat(
        [FromForm] int telegramChatId,
        CancellationToken cancellationToken)
    {
        var updated = await _telegramChats.SetActiveAsync(telegramChatId, cancellationToken);
        var model = updated is null
            ? await BuildTelegramChatsModelAsync("Telegram chat не знайдено.", true, cancellationToken)
            : await BuildTelegramChatsModelAsync("Telegram chat оновлено.", false, cancellationToken);

        return PartialView("_TelegramChats", model);
    }

    [HttpPost("user-configs/{id:int}/schedule")]
    public async Task<IActionResult> UpdateSchedule(
        int id,
        [FromForm] int dayOfWeek,
        [FromForm] int hour,
        CancellationToken cancellationToken)
    {
        var result = await _updateBookingSchedule.ExecuteAsync(id, dayOfWeek, hour, cancellationToken);
        var model = result.Status switch
        {
            UpdateBookingScheduleStatus.Updated => UserConfigScheduleViewModel.FromDomain(
                result.UserConfig!,
                "Збережено і перереєстровано."),
            UpdateBookingScheduleStatus.Invalid => await BuildErrorModelAsync(
                id,
                dayOfWeek,
                hour,
                result.Error!,
                cancellationToken),
            _ => new UserConfigScheduleViewModel(
                id,
                "Невідомо",
                DayOfWeek.Monday,
                0,
                result.Error,
                true)
        };

        return PartialView("_UserConfigRow", model);
    }

    private async Task<UserConfigScheduleViewModel> BuildErrorModelAsync(
        int id,
        int dayOfWeek,
        int hour,
        string error,
        CancellationToken cancellationToken)
    {
        var current = await _userConfigs.GetByIdAsync(id, cancellationToken);
        if (current is null)
        {
            return new UserConfigScheduleViewModel(
                id,
                "Невідомо",
                DayOfWeek.Monday,
                0,
                error,
                true);
        }

        var selectedDay = Enum.IsDefined(typeof(DayOfWeek), dayOfWeek)
            ? (DayOfWeek)dayOfWeek
            : current.DayOfWeek;

        var selectedHour = hour is >= 0 and <= 23
            ? hour
            : current.Hour;

        return UserConfigScheduleViewModel.FromDomain(
            current with { DayOfWeek = selectedDay, Hour = selectedHour },
            error,
            true);
    }

    private async Task<TelegramChatsViewModel> BuildTelegramChatsModelAsync(
        string? message,
        bool isError,
        CancellationToken cancellationToken)
    {
        var chats = await _telegramChats.GetAllAsync(cancellationToken);
        return new TelegramChatsViewModel(
            chats.Select(TelegramChatOptionViewModel.FromDomain).ToList(),
            message,
            isError);
    }
}
