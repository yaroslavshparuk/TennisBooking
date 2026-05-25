using Microsoft.Extensions.Logging;
using TennisBooking.Application.Abstractions;

namespace TennisBooking.Application.Booking;

public sealed class CancelBookingUseCase
{
    private readonly IBookingCancellationLinkRepository _links;
    private readonly ISkeddaClient _skedda;
    private readonly IBookingScheduler _scheduler;
    private readonly ILogger<CancelBookingUseCase> _logger;

    public CancelBookingUseCase(
        IBookingCancellationLinkRepository links,
        ISkeddaClient skedda,
        IBookingScheduler scheduler,
        ILogger<CancelBookingUseCase> logger)
    {
        _links = links;
        _skedda = skedda;
        _scheduler = scheduler;
        _logger = logger;
    }

    public async Task<CancelBookingStatus> ExecuteAsync(
        long chatId,
        int repliedTelegramMessageId,
        int cancelRequestMessageId,
        CancellationToken cancellationToken)
    {
        var link = await _links.GetByReplyAsync(chatId, repliedTelegramMessageId, cancellationToken);
        if (link is null)
            return CancelBookingStatus.NotFound;

        if (!link.CancelledAtUtc.HasValue)
        {
            var prepared = new PreparedBooking(
                link.UserConfig,
                link.Slot,
                new { },
                string.Empty,
                string.Empty,
                string.Empty);
            await _skedda.CancelAsync(prepared, link.SkeddaBookingId, cancellationToken);

            var marked = await _links.TryMarkCancelledAsync(
                chatId,
                repliedTelegramMessageId,
                cancelRequestMessageId,
                cancellationToken);
            if (!marked)
            {
                link = await _links.GetByReplyAsync(chatId, repliedTelegramMessageId, cancellationToken) ?? link;
                DeleteReminderJobs(link);
                return CancelBookingStatus.AlreadyCancelled;
            }

            link = await _links.GetByReplyAsync(chatId, repliedTelegramMessageId, cancellationToken) ?? link;
            DeleteReminderJobs(link);
            _logger.LogInformation("Cancellation completed for booking {SkeddaBookingId}", link.SkeddaBookingId);
            return CancelBookingStatus.Cancelled;
        }

        DeleteReminderJobs(link);
        return CancelBookingStatus.AlreadyCancelled;
    }

    private void DeleteReminderJobs(BookingCancellationLink link)
    {
        if (!string.IsNullOrWhiteSpace(link.AttendanceReminder24hJobId))
            _scheduler.DeleteAttendanceCheck(link.AttendanceReminder24hJobId);

        if (!string.IsNullOrWhiteSpace(link.AttendanceReminder2hJobId))
            _scheduler.DeleteAttendanceCheck(link.AttendanceReminder2hJobId);
    }
}

public enum CancelBookingStatus
{
    NotFound,
    Cancelled,
    AlreadyCancelled
}
