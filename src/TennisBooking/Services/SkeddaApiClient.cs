using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using TennisBooking.Options;

namespace TennisBooking.Services;

public class SkeddaApiClient : ISkeddaApiClient
{
    private const string AccountLoginPath = "/account/login";
    private const string LoginPath = "/logins";
    private const string GetBookingsPath = "/booking";
    private const string BookingPath = "/bookings";
    private const string CsrfHeaderName = "x-skedda-requestverificationtoken";
    private const string CookieHeaderName = "Cookie";
    private const string ApplicationCookieName = "X-Skedda-ApplicationCookie";
    private const string CsrfCookieName = "X-Skedda-RequestVerificationCookie";

    private readonly SkeddaOptions _opts;

    public SkeddaApiClient(IOptions<SkeddaOptions> opts)
    {
        _opts = opts.Value;
    }

    public async Task<SkeddaSession> LoginAndGetSessionAsync(string username, string password, CancellationToken ct)
    {
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        var baseUri = new Uri(_opts.ApiBaseUrl);
        using var client = new HttpClient(handler) { BaseAddress = baseUri };

        var openLoginPage = await client.GetAsync(AccountLoginPath, ct);
        openLoginPage.EnsureSuccessStatusCode();
        var loginHtml = await openLoginPage.Content.ReadAsStringAsync(ct);
        var requestVerificationToken = GetRequestVerificationToken(loginHtml);

        var loginReq = new HttpRequestMessage(HttpMethod.Post, LoginPath);
        loginReq.Headers.Add(CsrfHeaderName, requestVerificationToken);
        loginReq.Headers.Add(CookieHeaderName,
            $"{CsrfCookieName}={handler.CookieContainer.GetCookies(baseUri)[CsrfCookieName]!.Value}");

        loginReq.Content = new StringContent(
            JsonConvert.SerializeObject(new
            {
                login = new
                {
                    arbitraryerrors = (object?)null,
                    username,
                    password,
                    rememberMe = false
                }
            }),
            Encoding.UTF8,
            "application/json"
        );
        var loginResp = await client.SendAsync(loginReq, ct);
        loginResp.EnsureSuccessStatusCode();

        var csrfCookie = handler.CookieContainer.GetCookies(baseUri)[CsrfCookieName]!.Value;
        var applicationCookie = handler.CookieContainer.GetCookies(baseUri)[ApplicationCookieName]!.Value;

        var getBookingsReq = new HttpRequestMessage(HttpMethod.Get, GetBookingsPath);
        getBookingsReq.Headers.Add(CsrfHeaderName, requestVerificationToken);
        getBookingsReq.Headers.Add(CookieHeaderName,
            $"{CsrfCookieName}={csrfCookie}; " +
            $"{ApplicationCookieName}={applicationCookie}");
        var getBookingsResp = await client.SendAsync(getBookingsReq, ct);
        getBookingsResp.EnsureSuccessStatusCode();
        var bookingToken = GetRequestVerificationToken(await getBookingsResp.Content.ReadAsStringAsync(ct));

        return new SkeddaSession
        {
            RequestVerificationToken = bookingToken,
            CsrfCookie = csrfCookie,
            ApplicationCookie = applicationCookie
        };
    }

    public async Task BookAsync(SkeddaSession session, object body, CancellationToken ct)
    {
        var baseUri = new Uri(_opts.ApiBaseUrl);
        using var client = new HttpClient { BaseAddress = baseUri };

        var bookReq = new HttpRequestMessage(HttpMethod.Post, BookingPath);
        bookReq.Content = new StringContent(
            JsonConvert.SerializeObject(body),
            Encoding.UTF8,
            "application/json"
        );
        bookReq.Headers.Add(CsrfHeaderName, session.RequestVerificationToken);
        bookReq.Headers.Add(CookieHeaderName,
            $"{CsrfCookieName}={session.CsrfCookie}; " +
            $"{ApplicationCookieName}={session.ApplicationCookie}");
        var bookResp = await client.SendAsync(bookReq, ct);
        if (!bookResp.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Response from Skedda {await bookResp.Content.ReadAsStringAsync(ct)}");
        }
    }

    public static string GetRequestVerificationToken(string html)
    {
        var match = Regex.Match(
            html,
            """<input\s+name="__RequestVerificationToken"\s+type="hidden"\s+value="([^"]+)"\s*/?>""",
            RegexOptions.IgnoreCase
        );
        if (!match.Success)
            throw new InvalidOperationException("__RequestVerificationToken not found in login page HTML.");
        return match.Groups[1].Value;
    }
}
