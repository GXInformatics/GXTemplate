// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Services.Identity;
using Microsoft.AspNetCore.Identity;

namespace CleanArchitecture.Blazor.Server.UI.Hubs;

/// <summary>
/// Online-presence hub: who is signed in, scoped to the tenant they are signed in to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every recipient set in this file is a tenant group. There is no <c>Clients.All</c>, and adding
/// one is a tenancy defect.</b> Pass 23 §3.6 found six <c>Clients.All</c> broadcasts here; Pass 30
/// deleted four of them with the dead features they belonged to (chat and page-component presence,
/// both unreachable) and bounded the surviving two.
/// </para>
/// <para>
/// <b>Presence has no cross-tenant escape, deliberately.</b> Unlike the query surfaces Passes 27-29
/// widened for cross-tenant holders, presence is not a query: there is no request to authorise and
/// no record left behind. A continuous feed of another tenant's staff signing in and out supports no
/// administrative task and would be unauditable, so <c>Users.ViewOnlineStatus</c> decides <i>whether</i>
/// a user sees presence and never <i>whose</i>. If that is ever revisited, the change is to
/// <see cref="GroupFor"/> and <see cref="GetOnlineUsers"/> together - they must stay in step.
/// </para>
/// <para>
/// <b>Group membership is re-established on every connect, which is what makes reconnect and tenant
/// switching correct.</b> SignalR groups are keyed by connection id and do not survive a reconnect,
/// so <see cref="OnConnectedAsync"/> is the only place membership can be set; the client uses
/// <c>WithAutomaticReconnect</c>, and a reconnect arrives here as a new connection that is regrouped
/// from a freshly resolved <c>UserContext</c>. Tenant switching relies on the same path - see
/// <c>TenantSelector.razor</c>, which forces a full page load precisely so the circuit and this
/// connection are rebuilt.
/// </para>
/// </remarks>
[Authorize(AuthenticationSchemes = "Identity.Application")]
public class ServerHub : Hub<ISignalRHub>
{
    /// <summary>
    /// One live connection. <paramref name="TenantId"/> is recorded at connect time from the
    /// server-resolved <c>UserContext</c>, so disconnect can address the right group without a
    /// context that is already being torn down, and <see cref="GetOnlineUsers"/> can scope without a
    /// database round trip per candidate.
    /// </summary>
    private sealed record ConnectionUser(string UserId, string UserName, string? TenantId);

    private static readonly ConcurrentDictionary<string, ConnectionUser> OnlineUsers = new(StringComparer.Ordinal);

    /// <summary>Prefix for the group holding one tenant's connections.</summary>
    private const string TenantGroupPrefix = "tenant:";

    /// <summary>
    /// Group for connections whose principal belongs to no tenant.
    /// </summary>
    /// <remarks>
    /// A sentinel <i>group</i>, not a sentinel <i>tenant id</i>: it cannot collide with
    /// <see cref="TenantGroupPrefix"/> + any id, because every tenanted group starts with
    /// <c>"tenant:"</c> and this one does not. Tenantless users therefore see each other and nobody
    /// else, which follows Pass 29's reading that a null tenant is a real value meaning
    /// "installation-level" rather than either "sees everyone" or "sees nobody".
    /// </remarks>
    private const string NoTenantGroup = "tenant-none";

    private readonly IServiceScopeFactory _scopeFactory;

    public ServerHub(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// The group a connection in <paramref name="tenantId"/> belongs to.
    /// </summary>
    /// <remarks>
    /// The single definition of "same audience". <see cref="GetOnlineUsers"/> scopes by comparing
    /// group names rather than tenant ids so that the roster a client can pull and the events it is
    /// broadcast can never disagree - including about what a null tenant means.
    /// </remarks>
    internal static string GroupFor(string? tenantId) =>
        string.IsNullOrWhiteSpace(tenantId) ? NoTenantGroup : TenantGroupPrefix + tenantId;

    public override async Task OnConnectedAsync()
    {
        var connectionId = Context.ConnectionId;

        // Server-resolved, from the hub filter's database load. Never Context.User's tenant claim
        // and never a client-supplied value - see HubUserContext for why both are wrong.
        var user = Context.GetUserContext();
        var userName = user?.UserName ?? Context.User?.Identity?.Name ?? string.Empty;
        var userId = user?.UserId ?? userName;
        var group = GroupFor(user?.TenantId);

        // Join before broadcasting, so the connecting client receives its own Connect - the previous
        // Clients.All behaviour, preserved.
        await Groups.AddToGroupAsync(connectionId, group).ConfigureAwait(false);

        var wasAlreadyOnline = OnlineUsers.Any(x => string.Equals(x.Value.UserId, userId, StringComparison.Ordinal));

        // Registered before the broadcast, not after: the broadcast makes clients call
        // GetOnlineUsers, and under the previous ordering that snapshot could race the insert and
        // come back without the user it was announcing.
        OnlineUsers[connectionId] = new ConnectionUser(userId, userName, user?.TenantId);

        if (!wasAlreadyOnline)
        {
            await Clients.Group(group).Connect(connectionId, userName).ConfigureAwait(false);
        }

        await base.OnConnectedAsync().ConfigureAwait(false);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var connectionId = Context.ConnectionId;

        // Remove the connection and check if it was the last one for this user.
        if (OnlineUsers.TryRemove(connectionId, out var connectionUser))
        {
            if (!OnlineUsers.Any(x => string.Equals(x.Value.UserId, connectionUser.UserId, StringComparison.Ordinal)))
            {
                // Addressed from the tenant recorded at connect time rather than re-read from the
                // connection, which is being torn down. SignalR removes the connection from its
                // groups on disconnect, so no explicit RemoveFromGroupAsync is needed.
                await Clients.Group(GroupFor(connectionUser.TenantId))
                    .Disconnect(connectionId, connectionUser.UserName).ConfigureAwait(false);
            }
        }

        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }

    /// <summary>
    /// Client -> Server: a snapshot of the online users the caller is allowed to see.
    /// </summary>
    /// <remarks>
    /// <b>Groups do not reach this method and it needs its own bound.</b> It is invocable directly
    /// over the WebSocket and returns a projection of process-wide state, so no group membership and
    /// no UI gate constrains it - before Pass 30 it returned every online user in the installation
    /// with <c>TenantId</c>, <c>Email</c> and <c>SuperiorId</c>, to any authenticated connection.
    /// <para>
    /// It fails closed: an unresolvable connection scopes to the no-tenant group and sees only other
    /// unresolvable connections.
    /// </para>
    /// </remarks>
    public async Task<List<UserContext>> GetOnlineUsers()
    {
        var callerGroup = GroupFor(Context.GetUserContext()?.TenantId);

        var distinctUsers = OnlineUsers.Values
            .Where(v => string.Equals(GroupFor(v.TenantId), callerGroup, StringComparison.Ordinal))
            .GroupBy(v => v.UserId, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        using var scope = _scopeFactory.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var result = new List<UserContext>(distinctUsers.Count);
        foreach (var cu in distinctUsers.OrderBy(u => u.UserName, StringComparer.Ordinal))
        {
            var appUser = await userManager.FindByIdAsync(cu.UserId).ConfigureAwait(false);
            result.Add(new UserContext(
                UserId: cu.UserId,
                UserName: cu.UserName,
                DisplayName: appUser?.DisplayName,
                TenantId: appUser?.TenantId,
                Email: appUser?.Email,
                Roles: null,
                ProfilePictureDataUrl: appUser?.ProfilePictureDataUrl,
                SuperiorId: appUser?.SuperiorId
            ));
        }
        return result;
    }
}
