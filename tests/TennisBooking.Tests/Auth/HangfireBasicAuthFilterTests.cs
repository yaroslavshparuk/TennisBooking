using System.Text;
using FluentAssertions;
using Hangfire.Dashboard;
using Microsoft.AspNetCore.Http;
using TennisBooking.Auth;

namespace TennisBooking.Tests.Auth;

public class HangfireBasicAuthFilterTests
{
    private const string ValidUser = "admin";
    private const string ValidPass = "secret123";

    private readonly HangfireBasicAuthFilter _filter = new(ValidUser, ValidPass);

    private class TestDashboardContext : DashboardContext
    {
        private readonly HttpContext _httpContext;

        public TestDashboardContext(HttpContext httpContext)
            : base(null!, null!, null!)
        {
            _httpContext = httpContext;
        }

        public override HttpContext GetHttpContext() => _httpContext;
    }

    private static DashboardContext CreateDashboardContext(string? authorizationHeader = null)
    {
        var httpContext = new DefaultHttpContext();
        if (authorizationHeader != null)
        {
            httpContext.Request.Headers["Authorization"] = authorizationHeader;
        }

        return new TestDashboardContext(httpContext);
    }

    private static string EncodeBasicAuth(string user, string pass)
    {
        return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));
    }

    [Fact]
    public void Authorize_ValidCredentials_ReturnsTrue()
    {
        var context = CreateDashboardContext(EncodeBasicAuth(ValidUser, ValidPass));

        var result = _filter.Authorize(context);

        result.Should().BeTrue();
    }

    [Fact]
    public void Authorize_NoAuthHeader_ReturnsFalseAndSetsChallenge()
    {
        var context = CreateDashboardContext(null);

        var result = _filter.Authorize(context);

        result.Should().BeFalse();
        var httpContext = context.GetHttpContext();
        httpContext.Response.StatusCode.Should().Be(401);
        httpContext.Response.Headers["WWW-Authenticate"].ToString().Should().Contain("Basic");
    }

    [Fact]
    public void Authorize_NonBasicScheme_ReturnsFalseAndSetsChallenge()
    {
        var context = CreateDashboardContext("Bearer some-token");

        var result = _filter.Authorize(context);

        result.Should().BeFalse();
        context.GetHttpContext().Response.StatusCode.Should().Be(401);
    }

    [Fact]
    public void Authorize_WrongUsername_ReturnsFalse()
    {
        var context = CreateDashboardContext(EncodeBasicAuth("wrong", ValidPass));

        var result = _filter.Authorize(context);

        result.Should().BeFalse();
        context.GetHttpContext().Response.StatusCode.Should().Be(401);
    }

    [Fact]
    public void Authorize_WrongPassword_ReturnsFalse()
    {
        var context = CreateDashboardContext(EncodeBasicAuth(ValidUser, "wrongpass"));

        var result = _filter.Authorize(context);

        result.Should().BeFalse();
        context.GetHttpContext().Response.StatusCode.Should().Be(401);
    }

    [Fact]
    public void Authorize_EmptyCredentials_ReturnsFalse()
    {
        var encoded = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(":"));
        var context = CreateDashboardContext(encoded);

        var result = _filter.Authorize(context);

        result.Should().BeFalse();
    }

    [Fact]
    public void Authorize_PasswordWithColon_HandledCorrectly()
    {
        var filter = new HangfireBasicAuthFilter("user", "pass:with:colons");
        var context = CreateDashboardContext(EncodeBasicAuth("user", "pass:with:colons"));

        var result = filter.Authorize(context);

        result.Should().BeTrue();
    }
}
