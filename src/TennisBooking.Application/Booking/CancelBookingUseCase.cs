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
            // Inert placeholder: CancelAsync builds its own session and never reads BodyJson/CookieHeader.
            var prepared = new PreparedBooking(
                link.UserConfig,
                link.Slot,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty);
            try
            {
                await _skedda.CancelAsync(prepared, link.SkeddaBookingId, cancellationToken);
            }
            catch (SkeddaBookingRejectedException ex)
            {
                // Expected business outcome (e.g. Skedda "locked in" - online cancel not allowed).
                // Do not mark the link cancelled and keep reminders - the booking still exists.
                _logger.LogInformation(
                    ex,
                    "Skedda rejected cancellation for booking {SkeddaBookingId}",
                    link.SkeddaBookingId);
                return CancelBookingStatus.NotCancellable;
            }

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
    AlreadyCancelled,
    NotCancellable
}
