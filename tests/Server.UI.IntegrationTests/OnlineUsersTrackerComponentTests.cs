#nullable enable
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using Bunit.TestDoubles;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Server.UI.Components.Presence;
using CleanArchitecture.Blazor.Server.UI.Hubs;
using CleanArchitecture.Blazor.Server.UI.Services;
using CleanArchitecture.Blazor.Server.UI.Services.Layout;
using CleanArchitecture.Blazor.Server.UI.Services.UserPreferences;
using FluentAssertions;
using Mapster;
using Mediator;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// The permission gate on the online-users roster.
/// </summary>
/// <remarks>
/// <para>
/// Two components consume the same presence stream and, before Pass 30, only one of them checked
/// <c>Users.ViewOnlineStatus</c>: <c>UserLoginState</c> gated its sign-in toasts, while this
/// component - which renders the actual list of who is online, avatars and user names, inside the
/// theme drawer - checked nothing. The more revealing surface was the ungated one, so every
/// authenticated user could see the roster. That is a distinct defect from the tenancy one and would
/// have survived a perfect tenant-grouping implementation, which is why it has its own suite.
/// </para>
/// <para>
/// <b>The gate is observed by whether the component reaches the hub at all</b>, not by markup. An
/// empty roster and a forbidden roster both render nothing, so markup cannot tell them apart. What
/// separates them is that a holder starts the connection and pulls a snapshot while a non-holder
/// returns before either - and putting presence data on the wire and then declining to draw it would
/// be a fake gate. The connection here is built over a message handler that always throws a marked
/// exception, so "the component tried to connect" is a deterministic, socket-free observation.
/// </para>
/// </remarks>
[TestFixture]
public class OnlineUsersTrackerComponentTests
{
    /// <summary>Marker carried by the only exception this fixture's hub connection can produce.</summary>
    private const string ConnectionAttemptMarker = "online-users-tracker-connection-attempted";

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

        services.AddSingleton(Mock.Of<ISnackbar>());
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

        // The component reads the current user id from here purely to sort itself first.
        services.AddSingleton(Mock.Of<IUserContextAccessor>());

        // A real HubClient whose connection can never negotiate: every HTTP attempt throws a marked
        // exception, so starting is observable and nothing touches a socket.
        var factory = new Mock<IHubConnectionFactory>();
        factory.Setup(f => f.CreateForCurrentUser(It.IsAny<string>()))
            .Returns(() => new HubConnectionBuilder()
                .WithUrl("http://localhost/never-negotiated", options =>
                {
                    options.HttpMessageHandlerFactory = _ => new AlwaysThrowingHandler();
                })
                .Build());

        services.AddSingleton(new HubClient(factory.Object));
    }

    [TearDown]
    public async Task TearDown() => await _ctx.DisposeAsync();

    private void SignInWith(params string[] permissions)
    {
        var authorization = _ctx.AddAuthorization();
        authorization.SetAuthorized("tester");
        authorization.SetPolicies(permissions);
    }

    [Test]
    public void WithoutViewOnlineStatus_TheRosterIsNotRenderedAndNoPresenceDataIsRequested()
    {
        SignInWith();

        var cut = _ctx.Render<OnlineUsersTracker>();

        cut.Markup.Trim().Should().BeEmpty(
            "a user without Users.ViewOnlineStatus must not see who is online");

        // Reaching this line at all is the second half: had the component started the connection
        // before checking, the marked exception would have been thrown out of Render.
    }

    [Test]
    public void WithViewOnlineStatus_TheComponentConnectsAndAsksForTheRoster()
    {
        SignInWith(Permissions.Users.ViewOnlineStatus);

        var render = () => _ctx.Render<OnlineUsersTracker>();

        // Narrowed, not emptied. A gate that denied everyone would pass the test above and this one
        // is what stops it: a holder must still get as far as the hub. The roster stays empty here
        // only because this fixture's connection cannot negotiate.
        render.Should().Throw<Exception>()
            .Which.ToString().Should().Contain(ConnectionAttemptMarker,
                "a holder of Users.ViewOnlineStatus must actually start the presence connection");
    }

    [Test]
    public void TheRosterAppliesNoTenantFilterOfItsOwn()
    {
        // Deliberate, and worth pinning so it is not "fixed" later. The hub scopes GetOnlineUsers to
        // the caller's tenant; it is invocable directly over the WebSocket, so a filter added here
        // would constrain the display without constraining the disclosure, and would read as though
        // the bound lived in the UI.
        var source = System.IO.File.ReadAllText(SourcePath());

        source.Should().NotContain("TenantId",
            "the tenant bound on presence belongs to ServerHub, not to this component");
        source.Should().Contain(nameof(Permissions.Users.ViewOnlineStatus),
            "the permission gate does belong here - it decides whether, not whose");
    }

    private static string SourcePath()
    {
        const string relative = "src/Server.UI/Components/Presence/OnlineUsersTracker.razor";

        var directory = new System.IO.DirectoryInfo(
            System.IO.Path.GetDirectoryName(typeof(OnlineUsersTrackerComponentTests).Assembly.Location)!);

        while (directory is not null)
        {
            var candidate = System.IO.Path.Combine(
                directory.FullName, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (System.IO.File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new System.IO.FileNotFoundException(
            $"Could not find {relative} above the test assembly; this test fails rather than " +
            "silently testing nothing.");
    }

    /// <summary>Fails every request with a marked exception, so no socket is ever opened.</summary>
    private sealed class AlwaysThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(ConnectionAttemptMarker);
    }
}
