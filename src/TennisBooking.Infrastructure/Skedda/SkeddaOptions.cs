namespace TennisBooking.Options;

public class SkeddaOptions
{
    public string ApiBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Booking POST burst: the same booking is fired several times at these offsets (milliseconds)
    /// relative to the exact open instant, so that — despite a jittery residential connection — at
    /// least one request LANDS right as the slot opens. Negative = before open (compensates for the
    /// network flight time), positive = after. The burst stops as soon as one attempt succeeds. A
    /// single offset of 0 reproduces the original single-shot behaviour.
    ///
    /// Defaults to empty on purpose: the .NET configuration binder APPENDS bound array items to a
    /// non-empty default, which would make the value un-tunable. Empty here means a configured value
    /// fully replaces it, and when none is configured the scheduler falls back to its own defaults
    /// (PreciseBookingScheduler.DefaultBurstOffsetsMs) — the single source of the real defaults.
    /// </summary>
    public int[] BookingSendOffsetsMs { get; set; } = Array.Empty<int>();

    /// <summary>
    /// Deadline for the burst SEND window, measured from the open instant: an attempt whose POST is
    /// still in flight past this point is abandoned. Follow-ups (notification/DB/reminders) run outside
    /// this deadline. 0 means "use the scheduler default" (see PreciseBookingScheduler.DefaultBurstStopAfterMs).
    /// </summary>
    public int BookingSendStopAfterMs { get; set; }
}
