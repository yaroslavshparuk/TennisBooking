using System.Text;
using FluentAssertions;
using Hangfire;
using Hangfire.Dashboard;
using Microsoft.AspNetCore.Http;
using Moq;
using TennisBooking.Auth;

namespace TennisBooking.Tests.Auth;

public class HangfireBasicAuthFilterTests
{
    private const string ValidUser = "admin";
    private const string ValidPass = "secret123";

    private readonly HangfireBasicAuthFilter _filter = new(ValidUser, ValidPass);

    private static DashboardContext CreateDashboardContext(string? authorizationHeader = null)
    {
        var httpContext = new DefaultHttpContext();
        if (authorizationHeader != null)
        {
            httpContext.Request.Headers["Authorization"] = authorizationHeader;
        }

        var storage = new Mock<JobStorage>();
        return new AspNetCoreDashboardContext(storage.Object, new DashboardOptions(), httpContext);
    }

    private static string EncodeBasicAuth(string user, string pass)
    {
        return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));
    }

    [Fact]
    public void Authorize_ValidCredentials_ReturnsTrue()
    {
        var context = CreateDashboardContext(EncodeBasicAuth(ValidUser, ValidPass));
        _filter.Authorize(context).Should().BeTrue();
    }

    [Fact]
    public void Authorize_NoAuthHeader_ReturnsFalseAndSetsChallenge()
    {
        var context = CreateDashboardContext(null);
        _filter.Authorize(context).Should().BeFalse();
        var httpContext = context.GetHttpContext();
        httpContext.Response.StatusCode.Should().Be(401);
        httpContext.Response.Headers["WWW-Authenticate"].ToString().Should().Contain("Basic");
    }

    [Fact]
    public void Authorize_NonBasicScheme_ReturnsFalseAndSetsChallenge()
    {
        var context = CreateDashboardContext("Bearer some-token");
        _filter.Authorize(context).Should().BeFalse();
        context.GetHttpContext().Response.StatusCode.Should().Be(401);
    }

    [Fact]
    public void Authorize_WrongUsername_ReturnsFalse()
    {
        var context = CreateDashboardContext(EncodeBasicAuth("wrong", ValidPass));
        _filter.Authorize(context).Should().BeFalse();
        context.GetHttpContext().Response.StatusCode.Should().Be(401);
    }

    [Fact]
    public void Authorize_WrongPassword_ReturnsFalse()
    {
        var context = CreateDashboardContext(EncodeBasicAuth(ValidUser, "wrongpass"));
        _filter.Authorize(context).Should().BeFalse();
        context.GetHttpContext().Response.StatusCode.Should().Be(401);
    }

    [Fact]
    public void Authorize_EmptyCredentials_ReturnsFalse()
    {
        var encoded = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(":"));
        var context = CreateDashboardContext(encoded);
        _filter.Authorize(context).Should().BeFalse();
    }

    [Fact]
    public void Authorize_PasswordWithColon_HandledCorrectly()
    {
        var filter = new HangfireBasicAuthFilter("user", "pass:with:colons");
        var context = CreateDashboardContext(EncodeBasicAuth("user", "pass:with:colons"));
        filter.Authorize(context).Should().BeTrue();
    }
}
