#nullable enable
using System;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using Bunit;
using Bunit.TestDoubles;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Server.UI.Components.Identity;
using CleanArchitecture.Blazor.Server.UI.Hubs;
using CleanArchitecture.Blazor.Server.UI.Services;
using CleanArchitecture.Blazor.Server.UI.Services.Layout;
using CleanArchitecture.Blazor.Server.UI.Services.UserPreferences;
using FluentAssertions;
using Mapster;
using Mediator;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// The two permission decisions <see cref="UserLoginState"/> makes, observed where they are made.
/// </summary>
/// <remarks>
/// This component decides, entirely inside the circuit, whether a viewer is told that other people
/// are signing in and out. Both decisions are <c>AuthorizeAsync</c> calls in
/// <c>OnInitializedAsync</c>, and until now neither had a test - the same blind spot Pass 10 closed
/// for <c>AuthLayout</c>, where an HTTP response carries no component tree and so can see no
/// component decision.
/// <para>
/// The rules being pinned, which are not symmetric and are easy to get backwards:
/// <list type="bullet">
/// <item><c>Users.ViewOnlineStatus</c> gates BOTH notifications. Without it, nothing is shown.</item>
/// <item><c>Users.SuppressLoginNotification</c> additionally suppresses the LOGIN notification only.
/// A holder still sees logouts.</item>
/// <item>Login notifications are deduplicated per user per session; logouts are not.</item>
/// </list>
/// </para>
/// <para>
/// The events are raised through <c>HubClient</c>'s own private SignalR callbacks by reflection,
/// because a C# event can only be raised by its declaring type. That runs the real handler chain
/// rather than a reconstruction of it.
/// </para>
/// </remarks>
[TestFixture]
public class UserLoginStateComponentTests
{
    private BunitContext _ctx = null!;
    private HubClient _hubClient = null!;
    private Mock<ISnackbar> _snackbar = null!;
    private IRenderedComponent<UserLoginState> _cut = null!;

    [SetUp]
    public void SetUp()
    {
        _ctx = new BunitContext();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var services = _ctx.Services;
        services.AddLogging();
        services.AddLocalization();
        services.AddMudServices();

        // ISnackbar is REPLACED by a spy, registered after AddMudServices so it wins.
        //
        // Counting MudBlazor.ISnackbar.ShownSnackbars measures MudBlazor, not this component:
        // the real snackbar collapses identical messages, so two logouts for the same user show as
        // one. Setting PreventDuplicates=false looked like the fix and passed in isolation, then
        // failed in the full suite - the configuration is not as isolated per test as it appears.
        // Counting calls to Add removes the question entirely: what is asserted is what the
        // component DECIDED to raise, which is the thing under test.
        _snackbar = new Mock<ISnackbar>();
        services.AddSingleton(_snackbar.Object);

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

        // A real HubClient over a connection that is never started. The component only calls
        // StartAsync for an authenticated user, and these tests deliberately never authenticate for
        // that purpose - the permission decisions happen in OnInitializedAsync, before any of it.
        var factory = new Mock<IHubConnectionFactory>();
        factory.Setup(f => f.CreateForCurrentUser(It.IsAny<string>()))
            .Returns(() => new HubConnectionBuilder().WithUrl("http://localhost/never-started").Build());

        _hubClient = new HubClient(factory.Object);
        services.AddSingleton(_hubClient);
    }

    [TearDown]
    public async Task TearDown() => await _ctx.DisposeAsync();

    /// <summary>
    /// Signs in a user holding exactly the given permission claims. Not authenticated in the sense
    /// the component's OnAfterRenderAsync tests for, so no SignalR connection is attempted.
    /// </summary>
    private void SignInWith(params string[] permissions)
    {
        var authorization = _ctx.AddAuthorization();
        authorization.SetAuthorized("tester");
        authorization.SetPolicies(permissions);
    }

    /// <summary>Renders the component and keeps it, so the dispatcher can be flushed later.</summary>
    private void Render() => _cut = _ctx.Render<UserLoginState>();

    /// <summary>
    /// Raises the event SignalR would raise, then waits for the component to finish handling it.
    /// </summary>
    /// <remarks>
    /// A C# event can only be invoked by its declaring type, so the private callback the hub is
    /// wired to is invoked directly - that runs the real handler chain rather than a reconstruction.
    /// <para>
    /// The flush is not optional. Both handlers call <c>InvokeAsync</c>, which QUEUES the snackbar
    /// onto the renderer dispatcher rather than raising it inline, so asserting immediately counts
    /// whichever callbacks happened to have run - this test saw 3 of 4 without it, intermittently
    /// and only in a full-suite run. Dispatching an empty callback and waiting for it drains
    /// everything queued before it.
    /// </para>
    /// </remarks>
    private void RaiseHubEvent(string callback, string userName)
    {
        var method = typeof(HubClient).GetMethod(callback, BindingFlags.Instance | BindingFlags.NonPublic)
                     ?? throw new InvalidOperationException($"HubClient has no {callback}; the test needs updating.");

        method.Invoke(_hubClient, ["connection-id", userName]);

        _cut.InvokeAsync(() => { }).GetAwaiter().GetResult();
    }

    /// <summary>How many notifications the component decided to raise.</summary>
    private int Notifications => _snackbar.Invocations
        .Count(i => i.Method.Name == nameof(ISnackbar.Add));

    // ------------------------------------------------------------------ ViewOnlineStatus gates both

    [Test]
    public void WithoutViewOnlineStatus_NoLoginNotificationIsShown()
    {
        SignInWith();
        Render();

        RaiseHubEvent("OnLoginEventAsync", "someone");

        Notifications.Should().Be(0,
            "a viewer without Users.ViewOnlineStatus must not be told who is signing in");
    }

    [Test]
    public void WithoutViewOnlineStatus_NoLogoutNotificationIsShown()
    {
        SignInWith();
        Render();

        RaiseHubEvent("OnLogoutEventAsync", "someone");

        Notifications.Should().Be(0);
    }

    [Test]
    public void WithViewOnlineStatus_ALoginNotificationIsShown()
    {
        SignInWith(Permissions.Users.ViewOnlineStatus);
        Render();

        RaiseHubEvent("OnLoginEventAsync", "ada");

        Notifications.Should().Be(1);
    }

    [Test]
    public void WithViewOnlineStatus_ALogoutNotificationIsShown()
    {
        SignInWith(Permissions.Users.ViewOnlineStatus);
        Render();

        RaiseHubEvent("OnLogoutEventAsync", "ada");

        Notifications.Should().Be(1);
    }

    // ------------------------------------------------------------------ SuppressLoginNotification

    [Test]
    public void SuppressLoginNotification_SilencesLoginsOnly()
    {
        // The asymmetry, and the reason this is worth a test: the permission suppresses logins and
        // leaves logouts alone. Reading the component, that is one early return apart from
        // suppressing both.
        SignInWith(Permissions.Users.ViewOnlineStatus, Permissions.Users.SuppressLoginNotification);
        Render();

        RaiseHubEvent("OnLoginEventAsync", "ada");
        Notifications.Should().Be(0, "logins are suppressed for a holder of this permission");

        RaiseHubEvent("OnLogoutEventAsync", "ada");
        Notifications.Should().Be(1, "logouts are NOT suppressed by it");
    }

    [Test]
    public void SuppressLoginNotification_AloneStillShowsNothing()
    {
        // The paired negative: suppression is not a grant. Without ViewOnlineStatus the viewer sees
        // nothing either way.
        SignInWith(Permissions.Users.SuppressLoginNotification);
        Render();

        RaiseHubEvent("OnLoginEventAsync", "ada");
        RaiseHubEvent("OnLogoutEventAsync", "ada");

        Notifications.Should().Be(0);
    }

    // ------------------------------------------------------------------ deduplication

    [Test]
    public void ALoginIsAnnouncedOncePerUser_ButALogoutEveryTime()
    {
        // Deduplication applies to logins only. Worth pinning because the two paths look alike and
        // the difference is one HashSet.Add.
        SignInWith(Permissions.Users.ViewOnlineStatus);
        Render();

        RaiseHubEvent("OnLoginEventAsync", "ada");
        RaiseHubEvent("OnLoginEventAsync", "ada");
        Notifications.Should().Be(1, "a repeated login for the same user is announced once");

        RaiseHubEvent("OnLoginEventAsync", "grace");
        Notifications.Should().Be(2, "a different user is a different announcement");

        RaiseHubEvent("OnLogoutEventAsync", "ada");
        RaiseHubEvent("OnLogoutEventAsync", "ada");
        Notifications.Should().Be(4, "logouts are not deduplicated");
    }
}
#nullable restore
