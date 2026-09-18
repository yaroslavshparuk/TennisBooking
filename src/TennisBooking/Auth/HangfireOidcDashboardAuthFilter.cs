namespace TennisBooking.Auth;

using Hangfire.Dashboard;

/// <summary>
/// Hangfire dashboard gate backed by the app's OpenID Connect sign-in:
/// any authenticated user may view the dashboard. There is no separate
/// Hangfire username/password anymore.
/// </summary>
public sealed class HangfireOidcDashboardAuthFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var http = context.GetHttpContext();
        return http.User.Identity?.IsAuthenticated == true;
    }
}
