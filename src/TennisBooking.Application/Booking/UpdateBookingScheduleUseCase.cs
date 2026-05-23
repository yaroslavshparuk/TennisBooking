using TennisBooking.Application.Abstractions;
using TennisBooking.Domain.Booking;

namespace TennisBooking.Application.Booking;

public sealed class UpdateBookingScheduleUseCase
{
    private readonly IUserBookingConfigRepository _userConfigs;
    private readonly IBookingScheduler _bookingScheduler;

    public UpdateBookingScheduleUseCase(
        IUserBookingConfigRepository userConfigs,
        IBookingScheduler bookingScheduler)
    {
        _userConfigs = userConfigs;
        _bookingScheduler = bookingScheduler;
    }

    public async Task<UpdateBookingScheduleResult> ExecuteAsync(
        int userConfigId,
        int dayOfWeek,
        int hour,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(typeof(DayOfWeek), dayOfWeek))
            return UpdateBookingScheduleResult.Invalid("День тижня має бути від неділі до суботи.");

        if (hour is < 0 or > 23)
            return UpdateBookingScheduleResult.Invalid("Година має бути від 00 до 23.");

        var updated = await _userConfigs.UpdateScheduleAsync(userConfigId, (DayOfWeek)dayOfWeek, hour, cancellationToken);
        if (updated is null)
            return UpdateBookingScheduleResult.NotFound();

        _bookingScheduler.ScheduleRecurringPreparation(updated);
        return UpdateBookingScheduleResult.Updated(updated);
    }
}

public sealed record UpdateBookingScheduleResult(
    UpdateBookingScheduleStatus Status,
    BookingUserConfig? UserConfig,
    string? Error)
{
    public static UpdateBookingScheduleResult Updated(BookingUserConfig userConfig)
        => new(UpdateBookingScheduleStatus.Updated, userConfig, null);

    public static UpdateBookingScheduleResult Invalid(string error)
        => new(UpdateBookingScheduleStatus.Invalid, null, error);

    public static UpdateBookingScheduleResult NotFound()
        => new(UpdateBookingScheduleStatus.NotFound, null, "Налаштування бронювання не знайдено.");
}

public enum UpdateBookingScheduleStatus
{
    Updated,
    Invalid,
    NotFound
}
