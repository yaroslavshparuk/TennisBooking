using TennisBooking.DAL.Models;

namespace TennisBooking.Models;

public class BookingInfo {
    public UserConfig UserConfig { get; set; }
    public string RequestVerificationToken { get; set; }

    public string CsrfCookie { get; set; }

    public string ApplicationCookie { get; set; }

    public object Body { get; set; }

    public DateTimeOffset StartTime { get; set; }
}
