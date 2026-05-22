namespace TennisBooking.Options;

public class TelegramOptions
{
    public string BotToken { get; set; } = string.Empty;
    public long ChatId { get; set; }
    public long PollingLeaderLockKey { get; set; } = 842001;
    public int PollingStandbyRetrySeconds { get; set; } = 10;
    public int PollingConflictBackoffSeconds { get; set; } = 20;
}
