namespace TennisBooking.Auth;

using System.Text;
using Hangfire.Dashboard;

public class HangfireBasicAuthFilter : IDashboardAuthorizationFilter
{
    private readonly string _user;
    private readonly string _pass;

    public HangfireBasicAuthFilter(string user, string pass)
    {
        _user = user;
        _pass = pass;
    }

    public bool Authorize(DashboardContext context)
    {
        var http = context.GetHttpContext();

        if (!http.Request.Headers.TryGetValue("Authorization", out var header))
        {
            Challenge(http);
            return false;
        }

        if (!header.ToString().StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            Challenge(http);
            return false;
        }

        var credentials = Encoding.UTF8
            .GetString(Convert.FromBase64String(header.ToString().Substring(6)))
            .Split(':', 2);

        if (credentials.Length == 2 &&
            credentials[0] == _user &&
            credentials[1] == _pass)
        {
            return true;
        }

        Challenge(http);
        return false;
    }

    private static void Challenge(HttpContext http)
    {
        http.Response.Headers["WWW-Authenticate"] = "Basic realm=\"Hangfire Dashboard\"";
        http.Response.StatusCode = StatusCodes.Status401Unauthorized;
    }
}
