namespace TennisBooking.Services;

using System.Text.RegularExpressions;
using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Options;
using System.Text;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

public class BookingService : BackgroundService
{
    private readonly SkeddaOptions _opts;
    private readonly ILogger<BookingService> _logger;

    public BookingService(IOptions<SkeddaOptions> opts, ILogger<BookingService> logger)
    {
        _opts = opts.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await BookCourtAsync(DateTime.Now.AddHours(-20), stoppingToken);
            _logger.LogInformation("Booked court");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to book court for ");
        }
    }

    private async Task BookCourtAsync(DateTime slot, CancellationToken ct)
    {
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };

        var baseUri = new Uri(_opts.ApiBaseUrl);
        using var client = new HttpClient(handler) { BaseAddress = baseUri };

        var openLoginPage = await client.GetAsync("/account/login", ct);
        openLoginPage.EnsureSuccessStatusCode();
        var loginHtml = await openLoginPage.Content.ReadAsStringAsync(ct);
        var requestVerificationToken = GetRequestVerificationToken(loginHtml);
        var loginReq = new HttpRequestMessage(HttpMethod.Post, "/logins");
        loginReq.Headers.Add("x-skedda-requestverificationtoken", requestVerificationToken);
        loginReq.Headers.Add("Cookie", $"X-Skedda-RequestVerificationCookie={handler.CookieContainer.GetCookies(baseUri)[0].Value}");

        loginReq.Content = new StringContent(
            JsonConvert.SerializeObject(new {
                login = new {
                    arbitraryerrors = (object)null,
                    username      = _opts.Username,
                    password      = _opts.Password,
                    rememberMe    = false
                }
            }),
            Encoding.UTF8,
            "application/json"
        );
        var loginResp = await client.SendAsync(loginReq, ct);
        loginResp.EnsureSuccessStatusCode();


        var getBookingsReq = new HttpRequestMessage(HttpMethod.Get, "/booking");
        getBookingsReq.Headers.Add("x-skedda-requestverificationtoken", requestVerificationToken);
        getBookingsReq.Headers.Add("Cookie", $"X-Skedda-RequestVerificationCookie={handler.CookieContainer.GetCookies(baseUri)["X-Skedda-RequestVerificationCookie"].Value}; " +
                                         $"X-Skedda-ApplicationCookie={handler.CookieContainer.GetCookies(baseUri)["X-Skedda-ApplicationCookie"].Value}");
        var getBookingsResp = await client.SendAsync(getBookingsReq, ct);
        getBookingsResp.EnsureSuccessStatusCode();
        requestVerificationToken = GetRequestVerificationToken(await getBookingsResp.Content.ReadAsStringAsync(ct));
        var bookingBody = new {
            booking = new {
                addConference            = false,
                allowInviteOthers        = false,
                arbitraryerrors          = (object)null,
                attendees                = new object[]{},
                availabilityStatus       = 1,
                chargeTransactionId      = (object)null,
                checkInAudits            = (object)null,
                createdDate              = (object)null,
                customFields             = new object[]{},
                decoupleBooking          = (object)null,
                decoupleDate             = (object)null,
                end                      = "2025-05-26T12:00:00",
                endOfLastOccurrence      = (object)null,
                hideAttendees            = true,
                lockInMargin             = 1,
                paymentStatus            = 0,
                piId                     = (object)null,
                price                    = 0,
                recurrenceRule           = (object)null,
                spaces                   = new[] { _opts.ResourceId },
                start                    = "2025-05-26T11:00:00",
                stripPrivateEventDetails = false,
                syncType                 = (object)null,
                title                    = (object)null,
                type                     = 1,
                unrecognizedOrganizer    = false,
                venue                    = _opts.Venue,
                venueuser                = _opts.VenueUser
            }
        };

        var bookReq = new HttpRequestMessage(HttpMethod.Post, "/bookings");
        bookReq.Content = new StringContent(
            JsonConvert.SerializeObject(bookingBody),
            Encoding.UTF8,
            "application/json"
        );
        bookReq.Headers.Add("x-skedda-requestverificationtoken", requestVerificationToken);
        bookReq.Headers.Add("Cookie", $"X-Skedda-RequestVerificationCookie={handler.CookieContainer.GetCookies(baseUri)["X-Skedda-RequestVerificationCookie"].Value}; " +
                                         $"X-Skedda-ApplicationCookie={handler.CookieContainer.GetCookies(baseUri)["X-Skedda-ApplicationCookie"].Value}");

        var bookResp = await client.SendAsync(bookReq, ct);
        bookResp.EnsureSuccessStatusCode();
    }

    private string GetRequestVerificationToken(string loginHtml)
    {
        var match = Regex.Match(
            loginHtml,
            "<input\\s+name=\\\"__RequestVerificationToken\\\"\\s+type=\\\"hidden\\\"\\s+value=\\\"([^\\\"]+)\\\"",
            RegexOptions.IgnoreCase
        );
        if (!match.Success)
            throw new InvalidOperationException("__RequestVerificationToken not found in login page HTML.");
        var requestVerificationToken = match.Groups[1].Value;
        return requestVerificationToken;
    }
}
