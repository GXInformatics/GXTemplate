#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Services.Identity;
using CleanArchitecture.Blazor.Server.UI.Hubs;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// The tenant bound on SignalR presence: which group a connection joins, who its events reach, and
/// what <see cref="ServerHub.GetOnlineUsers"/> hands back.
/// </summary>
/// <remarks>
/// <para>
/// <b>What these tests can and cannot see.</b> A hub method is an ordinary method on an ordinary
/// object: <c>Context</c>, <c>Clients</c> and <c>Groups</c> are settable properties, so the hub can
/// be driven in-process with a real connection lifecycle and its recipient decisions observed
/// exactly as SignalR would make them. What cannot be reached here is everything outside the hub
/// class - that the ASP.NET Core group manager actually delivers only to a group's members, that a
/// browser's WebSocket reconnects and re-enters <c>OnConnectedAsync</c>, that a forced page load
/// really does tear the circuit down. Those are SignalR's and the browser's behaviour, not this
/// template's, and the two-browser checks in the pass report cover them instead. The line this
/// suite holds is: every recipient decision this code makes is asserted; no delivery is.
/// </para>
/// <para>
/// <b>Both halves, deliberately.</b> Asserting that an event was sent to <c>Clients.Group("x")</c>
/// proves the send site and nothing about membership - a connection that was never added to
/// <c>"x"</c> passes that assertion while receiving nothing, and a connection added to the wrong
/// group passes it while receiving someone else's. So group assignment is asserted at
/// <c>Groups.AddToGroupAsync</c> as well, and the two are checked against the same connection id.
/// </para>
/// <para>
/// <b>Narrowed is not emptied.</b> A hub that broadcast to nobody would satisfy every isolation
/// assertion here, so each negative has a positive beside it: the tenant-A colleague who must still
/// appear is asserted in the same tests that assert the tenant-B stranger must not.
/// </para>
/// <para>
/// <c>ServerHub.OnlineUsers</c> is process-wide static state, so every connection a test opens is
/// closed in <see cref="TearDown"/> through the real <c>OnDisconnectedAsync</c> - which also
/// exercises the disconnect path rather than reaching around it.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
public class ServerHubTenantIsolationTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

    private const string GroupA = "tenant:" + TenantA;
    private const string GroupB = "tenant:" + TenantB;
    private const string GroupNone = "tenant-none";

    private IServiceProvider _provider = null!;
    private readonly List<Connection> _open = new();

    [SetUp]
    public void SetUp()
    {
        // GetOnlineUsers resolves a UserManager from a scope to decorate the snapshot. The store is
        // a stub: these tests are about WHICH users come back, not what is attached to them.
        var store = new Mock<IUserStore<ApplicationUser>>();
        var userManager = new Mock<UserManager<ApplicationUser>>(
            store.Object, null!, null!, null!, null!, null!, null!, null!, null!);

        userManager.Setup(m => m.FindByIdAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => new ApplicationUser { Id = id, UserName = id });

        var services = new ServiceCollection();
        services.AddScoped(_ => userManager.Object);
        _provider = services.BuildServiceProvider();
    }

    [TearDown]
    public async Task TearDown()
    {
        foreach (var connection in Enumerable.Reverse(_open).ToList())
        {
            await connection.DisconnectAsync();
        }
        _open.Clear();
    }

    // ------------------------------------------------------------------ group assignment

    [Test]
    public async Task OnConnected_JoinsTheGroupForTheConnectionsOwnTenant()
    {
        var connection = await ConnectAsync("u1", "alice", TenantA);

        connection.Groups.Verify(
            g => g.AddToGroupAsync(connection.ConnectionId, GroupA, It.IsAny<CancellationToken>()),
            Times.Once,
            "the connection has to be IN the group its events are addressed to, or the send site " +
            "is correct and nobody receives anything");
    }

    [Test]
    public async Task OnConnected_JoinsTheSentinelGroupWhenThePrincipalHasNoTenant()
    {
        var connection = await ConnectAsync("u1", "alice", tenantId: null);

        connection.Groups.Verify(
            g => g.AddToGroupAsync(connection.ConnectionId, GroupNone, It.IsAny<CancellationToken>()),
            Times.Once,
            "a tenantless user is a representable state and must land somewhere bounded - not in " +
            "every tenant's group, and not in a group named from a null");
    }

    [Test]
    public void TheSentinelGroupCannotCollideWithAnyTenantsGroup()
    {
        // Not a style point. If a tenant id could ever produce the no-tenant group name, that
        // tenant's users and every unresolvable connection would share an audience.
        ServerHub.GroupFor(null).Should().Be(GroupNone);
        ServerHub.GroupFor(GroupNone).Should().NotBe(GroupNone);
        ServerHub.GroupFor("none").Should().NotBe(GroupNone);
        ServerHub.GroupFor(TenantA).Should().StartWith("tenant:");
        GroupNone.Should().NotStartWith("tenant:",
            "the sentinel is a separate namespace, which is what makes the disjointness structural");
    }

    // ------------------------------------------------------------------ the server's view wins

    [Test]
    public async Task OnConnected_IgnoresTheTenantClaimOnThePrincipalAndUsesTheResolvedContext()
    {
        // The principal says tenant B; the hub filter's database-resolved context says tenant A.
        // The claim is the more discoverable source and it is the wrong one: only TenantSwitchService
        // ever writes it, so it is absent for a user who has never switched and stale for one whose
        // cookie predates their last switch.
        var connection = await ConnectAsync("u1", "alice", TenantA, claimedTenantId: TenantB);

        connection.Groups.Verify(
            g => g.AddToGroupAsync(connection.ConnectionId, GroupA, It.IsAny<CancellationToken>()),
            Times.Once);
        connection.Groups.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), GroupB, It.IsAny<CancellationToken>()),
            Times.Never,
            "a tenant claim on the cookie must not be able to move a connection into another " +
            "tenant's presence group");
    }

    [Test]
    public void NoHubMethodTakesATenantFromTheClient()
    {
        // A hub method parameter naming a tenant is a client-supplied claim: a client talks to the
        // hub directly over the WebSocket, so no UI gate constrains what it may pass.
        var parameters = typeof(ServerHub)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(m => m.GetParameters().Select(p => $"{m.Name}({p.Name})"))
            .ToArray();

        parameters.Where(p => p.Contains("tenant", StringComparison.OrdinalIgnoreCase))
            .Should().BeEmpty("group membership is established from the server's view of the principal");
    }

    // ------------------------------------------------------------------ the send sites

    [Test]
    public async Task Connect_IsBroadcastToTheConnectionsTenantGroupAndNowhereElse()
    {
        var connection = await ConnectAsync("u1", "alice", TenantA);

        connection.Sent(GroupA).Verify(h => h.Connect(connection.ConnectionId, "alice"), Times.Once);
        connection.AssertNoBroadcastToAll();
        connection.AssertNothingSentTo(GroupB, GroupNone);
    }

    [Test]
    public async Task Disconnect_IsBroadcastToTheTenantGroupRecordedAtConnectTime()
    {
        var connection = await ConnectAsync("u1", "alice", TenantA);

        await connection.DisconnectAsync();

        connection.Sent(GroupA).Verify(h => h.Disconnect(connection.ConnectionId, "alice"), Times.Once);
        connection.AssertNoBroadcastToAll();
        connection.AssertNothingSentTo(GroupB, GroupNone);
    }

    [Test]
    public void ServerHubContainsNoBroadcastToEveryClient()
    {
        // Pass 29's shape for an exemption: an installation-wide broadcast would have to be added
        // deliberately and carry a comment saying why. There is none, so the literal must not appear
        // in code. Comment lines are stripped first - the file's own remarks name Clients.All in
        // order to forbid it, and a scan that could not tell the prohibition from a violation would
        // be permanently red and would then be deleted rather than believed.
        var code = ReadServerHubSource()
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal));

        string.Join("\n", code).Should().NotContain("Clients.All",
            "every recipient set in ServerHub is a tenant group; a Clients.All is a tenancy defect");
    }

    // ------------------------------------------------------------------ narrowed, not emptied

    [Test]
    public async Task AColleagueInTheSameTenantStillSeesTheArrivalAndTheDeparture()
    {
        await ConnectAsync("u1", "alice", TenantA);
        var bob = await ConnectAsync("u2", "bob", TenantA);

        bob.Sent(GroupA).Verify(h => h.Connect(bob.ConnectionId, "bob"), Times.Once,
            "isolation must narrow the audience, not empty it - a hub that broadcasts to nobody " +
            "satisfies every assertion above");

        await bob.DisconnectAsync();
        bob.Sent(GroupA).Verify(h => h.Disconnect(bob.ConnectionId, "bob"), Times.Once);
    }

    [Test]
    public async Task AUserInAnotherTenantIsAddressedInTheirOwnGroupOnly()
    {
        await ConnectAsync("u1", "alice", TenantA);
        var carol = await ConnectAsync("u3", "carol", TenantB);

        carol.Sent(GroupB).Verify(h => h.Connect(carol.ConnectionId, "carol"), Times.Once);
        carol.AssertNothingSentTo(GroupA, GroupNone);
        carol.AssertNoBroadcastToAll();
    }

    // ------------------------------------------------------------------ the snapshot method

    [Test]
    public async Task GetOnlineUsers_ReturnsTheCallersTenantAndOnlyTheCallersTenant()
    {
        await ConnectAsync("u1", "alice", TenantA);
        await ConnectAsync("u2", "bob", TenantA);
        await ConnectAsync("u3", "carol", TenantB);
        await ConnectAsync("u4", "dave", tenantId: null);

        var alice = await ConnectAsync("u5", "anne", TenantA);

        var snapshot = await alice.Hub.GetOnlineUsers();

        snapshot.Select(u => u.UserName).Should().BeEquivalentTo(new[] { "alice", "anne", "bob" },
            "groups do not reach a method's return value - this needs its own bound, and it must " +
            "still return the caller's own colleagues");
    }

    [Test]
    public async Task GetOnlineUsers_FromATenantlessConnectionSeesOnlyOtherTenantlessConnections()
    {
        await ConnectAsync("u1", "alice", TenantA);
        await ConnectAsync("u4", "dave", tenantId: null);

        var erin = await ConnectAsync("u6", "erin", tenantId: null);

        var snapshot = await erin.Hub.GetOnlineUsers();

        snapshot.Select(u => u.UserName).Should().BeEquivalentTo(new[] { "dave", "erin" });
    }

    [Test]
    public async Task GetOnlineUsers_FailsClosedWhenTheConnectionResolvesToNoUser()
    {
        await ConnectAsync("u1", "alice", TenantA);

        // No UserContext on the connection: the hub filter could not resolve the principal. This
        // must scope to the no-tenant audience, not to everyone.
        var unresolved = await ConnectAsync("u7", "frank", tenantId: null, attachUserContext: false);

        var snapshot = await unresolved.Hub.GetOnlineUsers();

        snapshot.Select(u => u.UserName).Should().NotContain("alice");
    }

    // ------------------------------------------------------------------ reconnect

    [Test]
    public async Task AReconnectRejoinsTheGroupUnderItsNewConnectionId()
    {
        // The client uses WithAutomaticReconnect, and a SignalR group is keyed by connection id, so
        // membership does not survive. A reconnect arrives as a disconnect followed by a connect
        // with a new id; if OnConnectedAsync did not re-add, the user would silently stop receiving.
        var first = await ConnectAsync("u1", "alice", TenantA);
        await first.DisconnectAsync();

        var second = await ConnectAsync("u1", "alice", TenantA);

        second.ConnectionId.Should().NotBe(first.ConnectionId);
        second.Groups.Verify(
            g => g.AddToGroupAsync(second.ConnectionId, GroupA, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ------------------------------------------------------------------ the tenant switch

    [Test]
    public async Task AfterSwitchingTenant_TheNewConnectionIsInTheNewTenantsGroupAndTheOldOneIsGone()
    {
        var before = await ConnectAsync("u1", "alice", TenantA);

        // A switch forces a full page load, which tears down the circuit: the scoped HubClient is
        // disposed and reconnects. That arrives here as a disconnect and a fresh connect whose
        // UserContext has been re-resolved - TenantSwitchService clears the loader's cache before
        // the component navigates, so the new tenant is what gets read.
        await before.DisconnectAsync();
        var after = await ConnectAsync("u1", "alice", TenantB);

        after.Groups.Verify(
            g => g.AddToGroupAsync(after.ConnectionId, GroupB, It.IsAny<CancellationToken>()),
            Times.Once);
        after.Groups.Verify(
            g => g.AddToGroupAsync(after.ConnectionId, GroupA, It.IsAny<CancellationToken>()),
            Times.Never,
            "the switched user must stop receiving the tenant they left");
    }

    [Test]
    public async Task AfterSwitchingTenant_TheSnapshotShowsTheNewTenantAndNotThePrevious()
    {
        await ConnectAsync("u2", "bob", TenantA);
        await ConnectAsync("u3", "carol", TenantB);

        var before = await ConnectAsync("u1", "alice", TenantA);
        (await before.Hub.GetOnlineUsers()).Select(u => u.UserName)
            .Should().BeEquivalentTo(new[] { "alice", "bob" });

        await before.DisconnectAsync();
        var after = await ConnectAsync("u1", "alice", TenantB);

        (await after.Hub.GetOnlineUsers()).Select(u => u.UserName)
            .Should().BeEquivalentTo(new[] { "alice", "carol" });
    }

    [Test]
    public void TheTenantSwitchStillForcesAFullPageLoad()
    {
        // The hub-level tests above assume the connection is rebuilt on a switch. Nothing in the hub
        // makes that happen - TenantSelector's forceLoad does, by destroying the circuit. A soft
        // navigation would keep the circuit, keep the connection, and leave the user in their
        // previous tenant's presence group with no test above failing. This is the pin.
        var source = ReadSource("src/Server.UI/Components/AppShell/TenantSelector.razor");

        source.Should().Contain("Navigation.NavigateTo(\"/\", true)",
            "forceLoad: true is what re-groups the SignalR connection after a tenant switch; " +
            "turning it into a soft navigation silently breaks presence isolation");
    }

    // ------------------------------------------------------------------ the dead surface is gone

    [Test]
    public void TheChatAndPageComponentSurfacesNoLongerExist()
    {
        // Pass 30 deleted these rather than isolating them: none had a caller anywhere in the
        // application, and between them they carried four of the six Clients.All broadcasts plus a
        // direct-message channel whose recipient was an unchecked, client-supplied user name.
        var hubMethods = typeof(ServerHub)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name);

        hubMethods.Should().NotContain(new[]
        {
            "SendMessage", "SendPrivateMessage", "SendNotification",
            "NotifyPageComponentOpen", "NotifyPageComponentClose"
        });

        typeof(ISignalRHub).GetMethods().Select(m => m.Name).Should().BeEquivalentTo(new[]
        {
            nameof(ISignalRHub.Connect), nameof(ISignalRHub.Disconnect), nameof(ISignalRHub.GetOnlineUsers)
        });

        typeof(HubClient).GetEvents().Select(e => e.Name).Should().BeEquivalentTo(new[]
        {
            nameof(HubClient.LoginEvent), nameof(HubClient.LogoutEvent)
        });
    }

    // ------------------------------------------------------------------ harness

    private int _nextConnection;

    /// <summary>
    /// Opens a connection through the real <c>OnConnectedAsync</c> and registers it for teardown.
    /// </summary>
    /// <param name="tenantId">The tenant the hub filter resolved for this principal.</param>
    /// <param name="claimedTenantId">A tenant claim on the cookie principal, which must be ignored.</param>
    /// <param name="attachUserContext">False to simulate a principal the filter could not resolve.</param>
    private async Task<Connection> ConnectAsync(
        string userId,
        string userName,
        string? tenantId,
        string? claimedTenantId = null,
        bool attachUserContext = true)
    {
        var connectionId = $"conn-{++_nextConnection}";

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(ClaimTypes.Name, userName)
        }, "TestAuth");

        if (claimedTenantId is not null)
        {
            identity.AddClaim(new Claim(ApplicationClaimTypes.TenantId, claimedTenantId));
        }

        var items = new Dictionary<object, object?>();
        if (attachUserContext)
        {
            items[HubUserContext.ItemsKey] = new UserContext(userId, userName, TenantId: tenantId);
        }

        var context = new Mock<HubCallerContext>();
        context.SetupGet(c => c.ConnectionId).Returns(connectionId);
        context.SetupGet(c => c.User).Returns(new ClaimsPrincipal(identity));
        context.SetupGet(c => c.Items).Returns(items);
        context.SetupGet(c => c.Features).Returns(new FeatureCollection());
        context.SetupGet(c => c.ConnectionAborted).Returns(CancellationToken.None);

        var connection = new Connection(connectionId, context.Object, _provider.GetRequiredService<IServiceScopeFactory>());
        _open.Add(connection);

        await connection.Hub.OnConnectedAsync();
        return connection;
    }

    /// <summary>One driven hub connection, with recording doubles for its recipient sets.</summary>
    private sealed class Connection
    {
        private readonly Dictionary<string, Mock<ISignalRHub>> _groups = new(StringComparer.Ordinal);
        private readonly Mock<IHubCallerClients<ISignalRHub>> _clients = new(MockBehavior.Strict);
        private bool _disconnected;

        public string ConnectionId { get; }
        public ServerHub Hub { get; }
        public Mock<IGroupManager> Groups { get; } = new();

        public Connection(string connectionId, HubCallerContext context, IServiceScopeFactory scopeFactory)
        {
            ConnectionId = connectionId;

            // Strict, so any recipient set the hub reaches for that is not set up here - Clients.All
            // above all - fails the test rather than quietly returning null.
            _clients.Setup(c => c.Group(It.IsAny<string>()))
                .Returns((string name) => GroupMock(name).Object);

            Hub = new ServerHub(scopeFactory)
            {
                Context = context,
                Clients = _clients.Object,
                Groups = Groups.Object
            };
        }

        /// <summary>The recording double for whatever was addressed to <paramref name="group"/>.</summary>
        public Mock<ISignalRHub> Sent(string group) => GroupMock(group);

        private Mock<ISignalRHub> GroupMock(string group)
        {
            if (!_groups.TryGetValue(group, out var mock))
            {
                mock = new Mock<ISignalRHub>();
                _groups[group] = mock;
            }
            return mock;
        }

        public void AssertNoBroadcastToAll() =>
            _clients.VerifyGet(c => c.All, Times.Never,
                "Clients.All reaches every tenant; there is no installation-wide presence event");

        public void AssertNothingSentTo(params string[] groups)
        {
            foreach (var group in groups)
            {
                _groups.TryGetValue(group, out var mock);
                mock?.Invocations.Should().BeEmpty(
                    $"nothing addressed to {group} may be raised by a connection outside it");
            }
        }

        public async Task DisconnectAsync()
        {
            if (_disconnected) return;
            _disconnected = true;
            await Hub.OnDisconnectedAsync(null);
        }
    }

    private static string ReadServerHubSource() => ReadSource("src/Server.UI/Hubs/ServerHub.cs");

    /// <summary>
    /// Reads a file from the repository, walking up from the test assembly until it appears.
    /// </summary>
    /// <remarks>
    /// Anchored on the path under <c>src/</c> rather than on a solution file or namespace, because
    /// both of those are renamed when the template is generated and the folder layout is not - so a
    /// generated project runs these against its own copy. Follows
    /// <c>GetDateRangeKindTests.SourcePath</c>.
    /// </remarks>
    private static string ReadSource(string relative)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(typeof(ServerHubTenantIsolationTests).Assembly.Location)!);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not find {relative} above {typeof(ServerHubTenantIsolationTests).Assembly.Location}. " +
            "This test reads the source so a reintroduction is caught; it fails rather than " +
            "silently testing nothing.");
    }
}
