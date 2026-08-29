#nullable enable
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using Bunit;
using Bunit.TestDoubles;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Server.UI.Layouts;
using CleanArchitecture.Blazor.Server.UI.Pages.Identity.Login;
using CleanArchitecture.Blazor.Server.UI.Services;
using CleanArchitecture.Blazor.Server.UI.Services.Layout;
using CleanArchitecture.Blazor.Server.UI.Services.UserPreferences;
using FluentAssertions;
using Mapster;
using Mediator;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor.Services;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// The authenticated-redirect guard in <see cref="AuthLayout" />, observed where it actually runs.
/// </summary>
/// <remarks>
/// Every other test in this solution observes HTTP. The application renders at
/// <c>InteractiveServerRenderMode(prerender: false)</c>, so an HTTP response for any routed page
/// carries the app shell and nothing else - no layout, no page, no navigation decision. A layout
/// that ejects an authenticated user is therefore invisible to all of them: the 200 is returned
/// before the component ever runs, and the redirect that follows happens inside the circuit, where
/// it never becomes a request.
/// <para>
/// These tests render the real components in-process instead, which is the only place that decision
/// can be seen. That gap - not the redirect itself - is what let the change-password loop survive
/// three passes of green HTTP evidence.
/// </para>
/// </remarks>
[TestFixture]
public class AuthLayoutComponentTests
{
    private BunitContext _ctx = null!;

    [SetUp]
    public void SetUp()
    {
        _ctx = new BunitContext();

        // The pages under test call into MudBlazor and the theme service, both of which reach for
        // JS. None of it affects the navigation decision, so it is answered permissively rather
        // than mocked call by call.
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        RegisterServices(_ctx.Services);
    }

    // LayoutService is IAsyncDisposable, which the synchronous Dispose refuses to unwind.
    [TearDown]
    public async Task TearDown() => await _ctx.DisposeAsync();

    /// <summary>
    /// The solution's root <c>_Imports.razor</c> injects a dozen services into EVERY component, so
    /// rendering even the smallest page needs all of them present. Only the ones the guard actually
    /// consults are real; the rest exist to let construction succeed.
    /// </summary>
    private static void RegisterServices(IServiceCollection services)
    {
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

        // ChangePassword resolves a UserManager in OnInitializedAsync. Rendering never touches the
        // store, so a stub one is enough to let the manager be constructed.
        services.AddSingleton(Mock.Of<IUserStore<ApplicationUser>>());
        services.AddIdentityCore<ApplicationUser>();
    }

    /// <summary>A body that is trivially findable, for asserting whether the layout rendered it.</summary>
    private static readonly RenderFragment Marker = builder =>
    {
        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "id", "body-marker");
        builder.CloseElement();
    };

    /// <summary>The real change-password page, as the router would supply it to this layout.</summary>
    private static readonly RenderFragment ChangePasswordPage = builder =>
    {
        builder.OpenComponent<ChangePassword>(0);
        builder.CloseComponent();
    };

    private BunitNavigationManager NavigateTo(string path)
    {
        var navigation = (BunitNavigationManager)_ctx.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(path);
        return navigation;
    }

    private void SignIn(params Claim[] claims)
    {
        var authorization = _ctx.AddAuthorization();
        authorization.SetAuthorized("administrator");
        if (claims.Length > 0) authorization.SetClaims(claims);
    }

    // ---------------------------------------------------------------- the defect

    [Test]
    public void AFlaggedUser_OnTheChangePasswordPage_IsNotSentAway()
    {
        // The browser-observed defect: the middleware holds a flagged user on this page, the layout
        // ejects them from it, and the two chase each other until Blazor gives up. The user ends on
        // the change-password URL with no form on it.
        SignIn(new Claim(ApplicationClaimTypes.MustChangePassword, "true"));
        var navigation = NavigateTo(ChangePassword.PageUrl);

        var cut = _ctx.Render<AuthLayout>(p => p.Add(l => l.Body, ChangePasswordPage));

        navigation.Uri.Should().EndWith(ChangePassword.PageUrl,
            "authentication is this page's precondition - ejecting an authenticated user from it is the loop");
        cut.Markup.Should().Contain("Choose a new password", "the page must render, not a redirect notice");
        cut.FindAll("input[type=password]").Count.Should().Be(3,
            "the form is the whole point of the page: current, new and confirm");
    }

    [Test]
    public void AnOrdinaryUser_ChangingTheirPasswordVoluntarily_ReachesTheSameForm()
    {
        // Changing your password is a normal action, not only a forced one. With no flag on the
        // principal the page must still render for a signed-in user who navigates to it themselves.
        SignIn();
        var navigation = NavigateTo(ChangePassword.PageUrl);

        var cut = _ctx.Render<AuthLayout>(p => p.Add(l => l.Body, ChangePasswordPage));

        navigation.Uri.Should().EndWith(ChangePassword.PageUrl);
        cut.FindAll("input[type=password]").Count.Should().Be(3);
    }

    // ---------------------------------------------------------------- the guard, where it belongs

    [Test]
    public void AnAuthenticatedUser_OnTheLoginPage_IsStillSentHome()
    {
        // The paired negative. The fix must not be "stop ejecting authenticated users": a signed-in
        // user who lands on the sign-in page still has no business there.
        SignIn();
        var navigation = NavigateTo(Login.PageUrl);

        var cut = _ctx.Render<AuthLayout>(p => p.Add(l => l.Body, Marker));

        navigation.Uri.Should().Be(navigation.BaseUri, "the guard sends an authenticated user home");
        cut.FindAll("#body-marker").Should().BeEmpty("the ejected page is never rendered");
    }

    [Test]
    public void AnAnonymousVisitor_OnTheLoginPage_IsLeftAlone()
    {
        // The guard is about authenticated users only; the page it guards must still work for the
        // people it was built for.
        _ctx.AddAuthorization().SetNotAuthorized();
        var navigation = NavigateTo(Login.PageUrl);

        var cut = _ctx.Render<AuthLayout>(p => p.Add(l => l.Body, Marker));

        navigation.Uri.Should().EndWith(Login.PageUrl);
        cut.FindAll("#body-marker").Should().ContainSingle();
    }

    // ---------------------------------------------------------------- the classification, route by route

    /// <summary>
    /// Routes under this layout that a signed-in user has no business on. Ejecting them is correct
    /// and must survive the fix.
    /// </summary>
    private static readonly string[] EjectedRoutes =
    [
        "/account/login",
        "/account/register",
        "/account/registerconfirmation",
        "/account/forgot-password",
        "/account/forgotpasswordconfirmation",
        "/account/reset-password",
        "/account/resetpasswordconfirmation",
        "/account/loginwith2fa",
        "/account/loginwithrecoverycode",
        "/account/lockout",
        "/account/invaliduser",
        "/account/linkexternallogin"
    ];

    /// <summary>
    /// Routes under this layout where being signed in is the precondition, not a reason to leave.
    /// </summary>
    private static readonly string[] ExemptRoutes =
    [
        "/account/change-password",
        "/account/confirmemail"
    ];

    [TestCaseSource(nameof(EjectedRoutes))]
    public void ARouteASignedInUserHasNoBusinessOn_StillEjectsThem(string route)
    {
        AuthLayout.RedirectsAuthenticatedUsersAwayFrom(route).Should().BeTrue();
    }

    [TestCaseSource(nameof(ExemptRoutes))]
    public void ARouteThatRequiresBeingSignedIn_DoesNotEjectThem(string route)
    {
        AuthLayout.RedirectsAuthenticatedUsersAwayFrom(route).Should().BeFalse();
    }

    [TestCase("account/change-password", TestName = "base-relative, the shape NavigationManager reports")]
    [TestCase("/account/change-password/", TestName = "trailing slash")]
    [TestCase("/account/Change-Password", TestName = "different casing")]
    [TestCase("/account/confirmemail?userId=1&code=2", TestName = "query string")]
    public void TheExemptionSurvives_TheShapesAPathActuallyArrivesIn(string path)
    {
        AuthLayout.RedirectsAuthenticatedUsersAwayFrom(path).Should().BeFalse();
    }

    [Test]
    public void EveryRoutedPageUnderThisLayout_IsClassifiedAbove()
    {
        // The guard is applied by folder: Pages/Identity/{Login,Register,Forgot}/_Imports.razor each
        // say "@layout AuthLayout", so a page joins it simply by being saved in one of those folders.
        // That is how the change-password page acquired it in the first place - nobody chose it for
        // that page. This asserts the reverse: nothing arrives under the guard unclassified.
        var routed = typeof(AuthLayout).Assembly
            .GetTypes()
            .Where(t => typeof(IComponent).IsAssignableFrom(t))
            .Where(t => t.GetCustomAttribute<LayoutAttribute>()?.LayoutType == typeof(AuthLayout))
            .SelectMany(t => t.GetCustomAttributes<RouteAttribute>().Select(r => r.Template))
            .ToArray();

        routed.Should().BeEquivalentTo(EjectedRoutes.Concat(ExemptRoutes));
    }

    [Test]
    public void TheChangePasswordPage_IsGovernedByThisLayout()
    {
        // Without this, the rendered tests above would be a composition of the tester's invention
        // rather than the one the router builds.
        typeof(ChangePassword).GetCustomAttribute<LayoutAttribute>()!.LayoutType
            .Should().Be<AuthLayout>();
    }
}
#nullable restore
