#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Server.UI.Pages.Identity.Forgot;
using CleanArchitecture.Blazor.Server.UI.Pages.Identity.Register;
using CleanArchitecture.Blazor.Server.UI.Services;
using CleanArchitecture.Blazor.Server.UI.Services.Layout;
using CleanArchitecture.Blazor.Server.UI.Services.UserPreferences;
using FluentAssertions;
using Mapster;
using Mediator;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor.Services;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// The component half of Pass 22's two ratified policies: which page performs which state change.
/// </summary>
/// <remarks>
/// <see cref="IdentityLifecyclePolicyTests"/> proves the gates hold end to end at the real login
/// endpoint. These prove the two components that MOVE the flags do the right thing, which the
/// end-to-end tests cannot see because they perform the state change themselves.
/// </remarks>
[TestFixture]
public class IdentityLifecycleComponentTests
{
    private BunitContext _ctx = null!;

    [SetUp]
    public void SetUp()
    {
        _ctx = new BunitContext();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var services = _ctx.Services;
        services.AddLogging();
        services.AddLocalization();
        services.AddMudServices();

        services.AddSingleton(Mock.Of<IUserPreferencesService>());
        services.AddScoped<LayoutService>();
        services.AddScoped<DialogServiceHelper>();
        services.AddSingleton(new TypeAdapterConfig());
        services.AddSingleton(Mock.Of<IApplicationSettings>());
        services.AddSingleton(Mock.Of<IUserProfileState>());
        services.AddSingleton(Mock.Of<IValidationService>());
        services.AddSingleton(Mock.Of<IMediator>());
        services.AddSingleton(Mock.Of<IAppCache>());
        services.AddSingleton(Mock.Of<IPermissionService>());
        services.AddSingleton(Mock.Of<IObjectMapper>());
    }

    [TearDown]
    public async Task TearDown() => await _ctx.DisposeAsync();

    private static Mock<UserManager<ApplicationUser>> NewUserManagerMock() =>
        new(Mock.Of<IUserStore<ApplicationUser>>(), null!, null!, null!, null!, null!, null!, null!, null!);

    private void NavigateTo(string relativeUrl) =>
        _ctx.Services.GetRequiredService<NavigationManager>().NavigateTo(relativeUrl);

    // ---- §A: a completed reset confirms the address ---------------------------------------------

    [Test]
    public void ResetPassword_ConfirmsTheAddress_WhenTheResetSucceeds()
    {
        var user = new ApplicationUser
        { Id = "u1", UserName = "someone", Email = "someone@example.com", EmailConfirmed = false };

        var users = NewUserManagerMock();
        users.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        users.Setup(m => m.ResetPasswordAsync(user, It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(IdentityResult.Success);

        ApplicationUser? updated = null;
        users.Setup(m => m.UpdateAsync(It.IsAny<ApplicationUser>()))
            .Callback<ApplicationUser>(u => updated = u)
            .ReturnsAsync(IdentityResult.Success);

        _ctx.Services.AddScoped(_ => users.Object);

        var token = WebEncoders.Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes("a-real-reset-token"));
        NavigateTo($"/account/reset-password?userid={user.Id}&token={token}");

        var cut = _ctx.Render<ResetPassword>();
        // Change, not Input: these fields are not Immediate, so they bind on onchange.
        foreach (var input in cut.FindAll("input[type=password]")) input.Change("Gx-New-Password-1!");
        cut.Find("form").Submit();

        users.Verify(m => m.ResetPasswordAsync(user, It.IsAny<string>(), It.IsAny<string>()), Times.Once,
            "the reset token must still be validated - confirming the address must not bypass it");

        updated.Should().NotBeNull("the address is confirmed by a completed reset");
        updated!.EmailConfirmed.Should().BeTrue(
            "redeeming a reset link proves mailbox control exactly as a confirmation link does");
    }

    [Test]
    public void ResetPassword_DoesNotConfirmTheAddress_WhenTheResetFails()
    {
        var user = new ApplicationUser
        { Id = "u1", UserName = "someone", Email = "someone@example.com", EmailConfirmed = false };

        var users = NewUserManagerMock();
        users.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        users.Setup(m => m.ResetPasswordAsync(user, It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(IdentityResult.Failed(new IdentityError { Description = "Invalid token." }));
        users.Setup(m => m.UpdateAsync(It.IsAny<ApplicationUser>())).ReturnsAsync(IdentityResult.Success);

        _ctx.Services.AddScoped(_ => users.Object);

        var token = WebEncoders.Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes("a-forged-token"));
        NavigateTo($"/account/reset-password?userid={user.Id}&token={token}");

        var cut = _ctx.Render<ResetPassword>();
        // Change, not Input: these fields are not Immediate, so they bind on onchange.
        foreach (var input in cut.FindAll("input[type=password]")) input.Change("Gx-New-Password-1!");
        cut.Find("form").Submit();

        users.Verify(m => m.UpdateAsync(It.IsAny<ApplicationUser>()), Times.Never,
            "a REJECTED reset must confirm nothing - otherwise anyone could confirm any address by "
            + "presenting a forged token");
        user.EmailConfirmed.Should().BeFalse();
    }

    // ---- §B: confirming an address is not granting access ---------------------------------------

    [Test]
    public void ConfirmEmail_ConfirmsTheAddress_ButDoesNotActivateTheAccount()
    {
        var user = new ApplicationUser
        {
            Id = "u2", UserName = "applicant", Email = "applicant@example.com",
            EmailConfirmed = false, IsActive = false
        };

        var users = NewUserManagerMock();
        users.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        users.Setup(m => m.ConfirmEmailAsync(user, It.IsAny<string>()))
            .ReturnsAsync(IdentityResult.Success);
        users.Setup(m => m.UpdateAsync(It.IsAny<ApplicationUser>())).ReturnsAsync(IdentityResult.Success);

        _ctx.Services.AddScoped(_ => users.Object);

        var code = WebEncoders.Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes("a-real-confirmation-token"));
        NavigateTo($"/account/confirmemail?userId={user.Id}&code={code}");

        _ctx.Render<ConfirmEmail>();

        user.IsActive.Should().BeFalse(
            "confirming an address proves the mailbox is yours; it does not decide that you may "
            + "have access. Self-registration exists so people can ASK for access - an "
            + "administrator grants it.");
    }
}
#nullable restore
