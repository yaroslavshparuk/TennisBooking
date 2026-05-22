using Hangfire;
using TennisBooking.Application.Abstractions;
using TennisBooking.Application.Booking;
using TennisBooking.Domain.Booking;

namespace TennisBooking.Infrastructure.Scheduling;

public sealed class HangfireBookingScheduler : IBookingScheduler
{
    private static readonly TimeZoneInfo KyivTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");

    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly IPreciseBookingScheduler _preciseBookingScheduler;

    public HangfireBookingScheduler(
        IBackgroundJobClient backgroundJobClient,
        IPreciseBookingScheduler preciseBookingScheduler)
    {
        _backgroundJobClient = backgroundJobClient;
        _preciseBookingScheduler = preciseBookingScheduler;
    }

    public void SchedulePreciseBooking(PreparedBooking booking)
        => _preciseBookingScheduler.ScheduleBooking(booking);

    public void ScheduleFallback(int userConfigId, DateTimeOffset startTime, DateTimeOffset runAt)
        => _backgroundJobClient.Schedule<BookingFallbackUseCase>(
            x => x.ExecuteAsync(userConfigId, startTime, CancellationToken.None),
            runAt);

    public void ScheduleAttendanceCheck(long chatId, int telegramMessageId, DateTimeOffset slotStartUtc, string reminderType, DateTimeOffset runAtUtc)
    {
        _ = slotStartUtc;
        var target = runAtUtc < DateTimeOffset.UtcNow
            ? DateTimeOffset.UtcNow.AddSeconds(1)
            : runAtUtc;

        _backgroundJobClient.Schedule<AttendanceReminderUseCase>(
            x => x.ExecuteAsync(chatId, telegramMessageId, reminderType, CancellationToken.None),
            target);
    }

    public void ScheduleRecurringPreparation(BookingUserConfig userConfig)
        => RecurringJob.AddOrUpdate<PrepareBookingForConfigUseCase>(
            userConfig.Username,
            x => x.ExecuteAsync(userConfig.Id, true, CancellationToken.None),
            Cron.Weekly(userConfig.DayOfWeek, userConfig.Hour - 1, 59),
            KyivTimeZone);
}
