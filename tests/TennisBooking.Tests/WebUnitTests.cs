using System.Reflection;
using System.Security.Claims;
using Hangfire;
using Hangfire.Dashboard;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using TennisBooking.Application.Abstractions;
using TennisBooking.Application.Booking;
using TennisBooking.Auth;
using TennisBooking.Controllers;
using TennisBooking.Domain.Booking;
using TennisBooking.Models;
using Xunit;

namespace TennisBooking.Tests;

public sealed class SettingsViewModelsTests
{
    [Fact]
    public void UserConfigScheduleViewModel_FromDomain_MapsFields_WithDefaults()
    {
        var config = new BookingUserConfig(3, "user", "p", "r", "v", "vu", DayOfWeek.Friday, 18);

        var model = UserConfigScheduleViewModel.FromDomain(config);

        Assert.Equal(3, model.Id);
        Assert.Equal("user", model.Username);
        Assert.Equal(DayOfWeek.Friday, model.DayOfWeek);
        Assert.Equal(18, model.Hour);
        Assert.Null(model.Message);
        Assert.False(model.IsError);
    }

    [Fact]
    public void UserConfigScheduleViewModel_FromDomain_PassesMessageAndErrorThrough()
    {
        var config = new BookingUserConfig(3, "user", "p", "r", "v", "vu", DayOfWeek.Friday, 18);

        var model = UserConfigScheduleViewModel.FromDomain(config, "Bad hour.", true);

        Assert.Equal("Bad hour.", model.Message);
        Assert.True(model.IsError);
    }

    [Fact]
    public void TelegramChatOptionViewModel_FromDomain_MapsFields()
    {
        var chat = new TelegramChat(2, "Family", 12345, true);

        var model = TelegramChatOptionViewModel.FromDomain(chat);

        Assert.Equal(2, model.Id);
        Assert.Equal("Family", model.Name);
        Assert.Equal(12345, model.ChatId);
        Assert.True(model.IsActive);
    }
}

public sealed class AuthOptionsTests
{
    [Fact]
    public void AuthOptions_HasExpectedDefaults()
    {
        var options = new AuthOptions();

        Assert.Equal("Auth", AuthOptions.SectionName);
        Assert.Equal(string.Empty, options.Authority);
        Assert.Equal(string.Empty, options.ClientId);
        Assert.Equal(new[] { "openid", "profile", "email" }, options.Scopes);
        Assert.Equal("name", options.NameClaimType);
        Assert.True(options.RequireHttpsMetadata);
        Assert.Equal(string.Empty, options.PublicBaseUrl);
        Assert.Null(options.GetRedirectUri("/signin-oidc"));
    }

    [Theory]
    [InlineData("https://tennis.example.com", "/signin-oidc", "https://tennis.example.com/signin-oidc")]
    [InlineData("https://tennis.example.com/", "/signout-callback-oidc", "https://tennis.example.com/signout-callback-oidc")]
    public void GetRedirectUri_PinsAbsoluteUri_WhenPublicBaseUrlSet(string baseUrl, string path, string expected)
    {
        var options = new AuthOptions { PublicBaseUrl = baseUrl };

        Assert.Equal(expected, options.GetRedirectUri(path));
    }
}

public sealed class HangfireDashboardAuthFilterExtraTests
{
    [Fact]
    public void Authorize_Denies_WhenIdentityIsNull()
    {
        var filter = new HangfireOidcDashboardAuthFilter();
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(), // no identities -> Identity is null
            RequestServices = new NullWebServiceProvider()
        };
        Assert.Null(http.User.Identity);

        var authorized = filter.Authorize(
            new AspNetCoreDashboardContext(Mock.Of<JobStorage>(), new DashboardOptions(), http));

        Assert.False(authorized);
    }
}

public sealed class AccountControllerTests
{
    [Fact]
    public void Login_DefaultsReturnUrl_ToSlash()
    {
        var controller = new AccountController();

        var result = Assert.IsType<ChallengeResult>(controller.Login(null));

        Assert.Contains(OpenIdConnectDefaults.AuthenticationScheme, result.AuthenticationSchemes);
        Assert.Equal("/", result.Properties!.RedirectUri);
    }

    [Fact]
    public void Login_PassesReturnUrlThrough()
    {
        var controller = new AccountController();

        var result = Assert.IsType<ChallengeResult>(controller.Login("/settings"));

        Assert.Equal("/settings", result.Properties!.RedirectUri);
    }

    [Fact]
    public async Task Logout_SignsOutOfBothSchemes_ThenRedirectsHome()
    {
        var auth = new Mock<IAuthenticationService>();
        auth.Setup(x => x.SignOutAsync(
                It.IsAny<HttpContext>(), It.IsAny<string>(), It.IsAny<AuthenticationProperties>()))
            .Returns(Task.CompletedTask);
        var controller = new AccountController();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { RequestServices = new SingleServiceProvider(auth.Object) }
        };

        var result = await controller.Logout();

        Assert.Equal("/", Assert.IsType<RedirectResult>(result).Url);
        auth.Verify(
            x => x.SignOutAsync(
                It.IsAny<HttpContext>(), CookieAuthenticationDefaults.AuthenticationScheme, It.IsAny<AuthenticationProperties>()),
            Times.Once);
        auth.Verify(
            x => x.SignOutAsync(
                It.IsAny<HttpContext>(), OpenIdConnectDefaults.AuthenticationScheme,
                It.Is<AuthenticationProperties>(p => p.RedirectUri == "/")),
            Times.Once);
    }

    [Fact]
    public void Logout_RequiresAntiForgeryToken()
    {
        var method = typeof(AccountController).GetMethod(nameof(AccountController.Logout));

        Assert.NotNull(method!.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
    }

    [Fact]
    public void SignedOut_RedirectsHome()
    {
        var controller = new AccountController();

        Assert.Equal("/", Assert.IsType<RedirectResult>(controller.SignedOut()).Url);
    }

    private sealed class SingleServiceProvider : IServiceProvider
    {
        private readonly IAuthenticationService _auth;
        public SingleServiceProvider(IAuthenticationService auth) => _auth = auth;
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IAuthenticationService) ? _auth : null;
    }

}

file sealed class NullWebServiceProvider : IServiceProvider
{
    public object? GetService(Type serviceType) => null;
}

public sealed class SettingsControllerTests
{
    [Fact]
    public async Task Index_MapsConfigsAndChats()
    {
        var configs = new[]
        {
            new BookingUserConfig(1, "u1", "p", "r", "v", "vu", DayOfWeek.Monday, 10),
            new BookingUserConfig(2, "u2", "p", "r", "v", "vu", DayOfWeek.Wednesday, 18)
        };
        var controller = NewController(
            configs: configs,
            chats: new[] { new TelegramChat(1, "Main", 111, true) });

        var result = Assert.IsType<ViewResult>(await controller.Index(CancellationToken.None));
        var model = Assert.IsType<SettingsIndexViewModel>(result.Model);

        Assert.Equal(2, model.UserConfigs.Count);
        Assert.Equal("u1", model.UserConfigs[0].Username);
        Assert.Single(model.TelegramChats.Chats);
        Assert.Null(model.TelegramChats.Message);
        Assert.False(model.TelegramChats.IsError);
    }

    [Fact]
    public async Task Index_EmptyDb_ReturnsEmptyModel()
    {
        var controller = NewController();

        var result = Assert.IsType<ViewResult>(await controller.Index(CancellationToken.None));
        var model = Assert.IsType<SettingsIndexViewModel>(result.Model);

        Assert.Empty(model.UserConfigs);
        Assert.Empty(model.TelegramChats.Chats);
    }

    [Fact]
    public async Task UpdateTelegramChat_ReportsNotFound_AsErrorPartial()
    {
        var controller = NewController(chats: Array.Empty<TelegramChat>(), activeChat: null);

        var result = Assert.IsType<PartialViewResult>(
            await controller.UpdateTelegramChat(99, CancellationToken.None));
        var model = Assert.IsType<TelegramChatsViewModel>(result.Model);

        Assert.Equal("_TelegramChats", result.ViewName);
        Assert.True(model.IsError);
        Assert.False(string.IsNullOrWhiteSpace(model.Message));
    }

    [Fact]
    public async Task UpdateTelegramChat_ReportsSuccess_WhenChatActivated()
    {
        var activated = new TelegramChat(1, "Main", 111, true);
        var controller = NewController(
            chats: new[] { activated }, activeChat: activated);

        var result = Assert.IsType<PartialViewResult>(
            await controller.UpdateTelegramChat(1, CancellationToken.None));
        var model = Assert.IsType<TelegramChatsViewModel>(result.Model);

        Assert.Equal("_TelegramChats", result.ViewName);
        Assert.False(model.IsError);
        Assert.False(string.IsNullOrWhiteSpace(model.Message));
    }

    [Fact]
    public async Task UpdateSchedule_Updated_RendersSavedRow()
    {
        var updated = new BookingUserConfig(1, "u", "p", "r", "v", "vu", DayOfWeek.Friday, 18);
        var controller = NewController(updatedScheduleResult: updated);

        var result = Assert.IsType<PartialViewResult>(
            await controller.UpdateSchedule(1, (int)DayOfWeek.Friday, 18, CancellationToken.None));
        var model = Assert.IsType<UserConfigScheduleViewModel>(result.Model);

        Assert.Equal("_UserConfigRow", result.ViewName);
        Assert.Equal(DayOfWeek.Friday, model.DayOfWeek);
        Assert.Equal(18, model.Hour);
        Assert.False(model.IsError);
        Assert.False(string.IsNullOrWhiteSpace(model.Message));
    }

    [Fact]
    public async Task UpdateSchedule_InvalidWithUnknownConfig_RendersPlaceholderError()
    {
        var controller = NewController(getById: null); // invalid hour + missing config

        var result = Assert.IsType<PartialViewResult>(
            await controller.UpdateSchedule(42, (int)DayOfWeek.Monday, 24, CancellationToken.None));
        var model = Assert.IsType<UserConfigScheduleViewModel>(result.Model);

        Assert.Equal("_UserConfigRow", result.ViewName);
        Assert.Equal(42, model.Id);
        Assert.Equal("Невідомо", model.Username);
        Assert.True(model.IsError);
    }

    [Fact]
    public async Task UpdateSchedule_InvalidWithKnownConfig_FallsBackToCurrentValues()
    {
        var current = new BookingUserConfig(1, "u", "p", "r", "v", "vu", DayOfWeek.Wednesday, 9);
        var controller = NewController(getById: current);

        // Day 99 / hour 99 are invalid for the update but must not leak into the re-rendered row.
        var result = Assert.IsType<PartialViewResult>(
            await controller.UpdateSchedule(1, 99, 99, CancellationToken.None));
        var model = Assert.IsType<UserConfigScheduleViewModel>(result.Model);

        Assert.Equal("_UserConfigRow", result.ViewName);
        Assert.Equal(DayOfWeek.Wednesday, model.DayOfWeek);
        Assert.Equal(9, model.Hour);
        Assert.True(model.IsError);
    }

    private static SettingsController NewController(
        IReadOnlyList<BookingUserConfig>? configs = null,
        IReadOnlyList<TelegramChat>? chats = null,
        TelegramChat? activeChat = null,
        BookingUserConfig? getById = null,
        BookingUserConfig? updatedScheduleResult = null)
    {
        var userConfigs = new Mock<IUserBookingConfigRepository>();
        userConfigs.Setup(x => x.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(configs ?? Array.Empty<BookingUserConfig>());
        userConfigs.Setup(x => x.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(getById);
        if (updatedScheduleResult is not null)
            userConfigs.Setup(x => x.UpdateScheduleAsync(
                    It.IsAny<int>(), It.IsAny<DayOfWeek>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(updatedScheduleResult);

        var telegramChats = new Mock<ITelegramChatRepository>();
        telegramChats.Setup(x => x.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(chats ?? Array.Empty<TelegramChat>());
        telegramChats.Setup(x => x.SetActiveAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(activeChat);

        var updateSchedule = new UpdateBookingScheduleUseCase(
            userConfigs.Object, Mock.Of<IBookingScheduler>());
        return new SettingsController(userConfigs.Object, telegramChats.Object, updateSchedule);
    }
}
