#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Models;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Application.Features.SecuritySettings.Queries;
using CleanArchitecture.Blazor.Infrastructure.Configurations;
using CleanArchitecture.Blazor.Server.UI.Pages.SystemManagement;
using CleanArchitecture.Blazor.Server.UI.Services;
using CleanArchitecture.Blazor.Server.UI.Services.Layout;
using CleanArchitecture.Blazor.Server.UI.Services.UserPreferences;
using FluentAssertions;
using Mapster;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor.Services;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// What the security-settings screen offers with and without
/// <c>Permissions.SecuritySettings.ManageInstallationPolicy</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only, never a control that refuses on save.</b> This template's own precedent, set by the
/// idle-timeout work: a surface a principal may not use is absent or inert. The whole page 404s when
/// the feature is switched off and the profile's Security tab is omitted rather than disabled - so an
/// editable field that produced an error on Save would be the one shape ruled out. These tests pin
/// the choice.
/// </para>
/// <para>
/// <b>Second line, not the boundary.</b> The rule that decides is
/// <c>InstallationPolicyWrite</c> inside <c>UpdateSecurityPolicyCommandHandler</c>, proved in
/// <c>InstallationPolicyWriteTests</c>, which drives the handler directly - the command is reachable
/// through Mediator whatever this page renders. What these tests hold is that the page does not
/// offer an edit it knows will be refused, and says why.
/// </para>
/// <para>
/// <b>Only rendering can see it.</b> The application renders at
/// <c>InteractiveServerRenderMode(prerender: false)</c>, so an HTTP response carries the shell and
/// none of this.
/// </para>
/// </remarks>
[TestFixture]
public class SecuritySettingsPermissionComponentTests
{
    private BunitContext _ctx = null!;

    [TearDown]
    public async Task TearDown() => await _ctx.DisposeAsync();

    /// <summary>
    /// Everything is granted except the one right under test, so a missing affordance is
    /// attributable to <c>ManageInstallationPolicy</c> and to nothing else.
    /// </summary>
    private void Arrange(bool mayManageInstallationPolicy, bool mayEdit = true)
    {
        _ctx = new BunitContext();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var services = _ctx.Services;
        services.AddLogging();
        services.AddLocalization();
        services.AddMudServices();

        services.AddSingleton(new TypeAdapterConfig());
        services.AddSingleton(Mock.Of<IObjectMapper>());
        services.AddSingleton(Mock.Of<IValidationService>());
        services.AddSingleton(Mock.Of<IAppCache>());
        services.AddSingleton(Mock.Of<IAuthorizationService>());
        services.AddScoped<DialogServiceHelper>();
        services.AddScoped<LayoutService>();
        services.AddSingleton(Mock.Of<IUserPreferencesService>());
        services.AddSingleton(Mock.Of<IApplicationSettings>());

        var profile = new Mock<IUserProfileState>();
        profile.SetupGet(x => x.Value).Returns(UserProfile.Empty);
        services.AddSingleton(profile.Object);

        var dto = new SecurityPolicyDto
        {
            IdleTimeoutMinutes = 15,
            CountdownSeconds = 60,
            Enabled = true,
            MinIdleTimeoutMinutes = 1,
            MaxIdleTimeoutMinutes = 120,
            AllowUserOverride = true
        };

        var mediator = new Mock<IMediator>();
        mediator.Setup(x => x.Send(It.IsAny<GetSecurityPolicyQuery>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<Result<SecurityPolicyDto>>(Result<SecurityPolicyDto>.Success(dto)));
        services.AddSingleton(mediator.Object);

        var permissions = new Mock<IPermissionService>();
        permissions.Setup(x => x.GetAccessRightsAsync<SecuritySettingsAccessRights>())
            .ReturnsAsync(new SecuritySettingsAccessRights
            {
                View = true,
                Edit = mayEdit,
                ManageInstallationPolicy = mayManageInstallationPolicy
            });
        services.AddSingleton(permissions.Object);
    }

    private IRenderedComponent<SecuritySettings> Render() => _ctx.Render<SecuritySettings>();

    /// <summary>The explanatory sentence shown to a principal who may not write.</summary>
    private const string Explanation = "manage installation policy";

    // ---- the affordance -------------------------------------------------------------------------

    [Test]
    public void WithoutTheRight_ThereIsNoSaveButton()
    {
        Arrange(mayManageInstallationPolicy: false);

        Render().Markup.Should().NotContain("Save",
            "a save that is known to be refused should not be offered at all");
    }

    [Test]
    public void WithTheRight_TheSaveButtonIsOffered()
    {
        // Narrowed, not emptied: the negative above must not be satisfied by a screen that offers
        // nothing to anybody.
        Arrange(mayManageInstallationPolicy: true);

        Render().Markup.Should().Contain("Save");
    }

    [Test]
    public void WithoutTheRight_TheFieldsAreReadOnly()
    {
        // MudBlazor renders readonly on the input element. Two fields, both of them.
        Arrange(mayManageInstallationPolicy: false);

        var inputs = Render().FindAll("input");

        inputs.Should().HaveCount(2, "idle minutes and countdown seconds");
        inputs.Should().OnlyContain(i => i.HasAttribute("readonly"));
    }

    [Test]
    public void WithTheRight_TheFieldsAreEditable()
    {
        Arrange(mayManageInstallationPolicy: true);

        var inputs = Render().FindAll("input");

        inputs.Should().HaveCount(2);
        inputs.Should().NotContain(i => i.HasAttribute("readonly"));
    }

    // ---- what a non-holder is told ---------------------------------------------------------------

    [Test]
    public void WithoutTheRight_TheScreenSaysWhy()
    {
        Arrange(mayManageInstallationPolicy: false);

        var markup = Render().Markup;

        markup.Should().Contain(Explanation,
            "a form that has quietly gone read-only reads as a bug unless it says why");
        markup.Should().Contain("every organisation",
            "the surprising part is the scope, not the refusal");
    }

    [Test]
    public void WithTheRight_NoSuchNoticeIsShown()
    {
        Arrange(mayManageInstallationPolicy: true);

        Render().Markup.Should().NotContain(Explanation);
    }

    // ---- reading is untouched --------------------------------------------------------------------

    [Test]
    public void WithoutTheRight_ThePolicyIsStillShown()
    {
        // THE control. A guard that also hid the values would satisfy every negative above while
        // removing the screen's purpose: an administrator needs to see the window to answer "why
        // was I signed out?", whoever may change it.
        Arrange(mayManageInstallationPolicy: false);

        var markup = Render().Markup;

        markup.Should().Contain("15", "the administered idle window is still displayed");
        markup.Should().Contain("60", "so is the countdown");
    }

    // ---- the two rights are distinct ------------------------------------------------------------

    [Test]
    public void HoldingTheScopeRightWithoutEditStillOffersNothing()
    {
        // Edit is the feature right and ManageInstallationPolicy the scope right; the write needs
        // both, and neither alone opens the form.
        Arrange(mayManageInstallationPolicy: true, mayEdit: false);

        var markup = Render().Markup;

        markup.Should().NotContain("Save");
        Render().FindAll("input").Should().OnlyContain(i => i.HasAttribute("readonly"));
    }
}
