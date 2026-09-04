using Microsoft.AspNetCore.SignalR;

namespace CleanArchitecture.Blazor.Infrastructure.Services.Identity;

/// <summary>
/// Reads the server-resolved <see cref="UserContext"/> that <see cref="UserContextHubFilter"/>
/// attaches to a SignalR connection.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the only supported way for a hub to learn who is on a connection, and the only source
/// a tenant decision may be made from.</b> Three plausible-looking alternatives are all wrong here:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>A hub method parameter naming a tenant</b> is a client-supplied claim. A client speaks to the
/// hub directly over the WebSocket; nothing the UI does constrains what it may send.
/// </item>
/// <item>
/// <b><c>Context.User</c>'s tenant claim</b> is absent or stale for most users.
/// <c>ApplicationUserClaimsPrincipalFactory</c> does not add one; the only writer of
/// <c>ApplicationClaimTypes.TenantId</c> is <c>TenantSwitchService.RefreshUserClaimsAsync</c>, so a
/// user who has never switched tenant carries no claim at all, and one who has carries whatever was
/// true when their cookie was minted. Keying groups off the principal is the more discoverable
/// implementation and it would be silently wrong.
/// </item>
/// <item>
/// <b><c>IUserContextAccessor.Current</c></b> works inside hub <i>methods</i> - the filter pushes the
/// ambient <c>AsyncLocal</c> in <c>InvokeMethodAsync</c> - but is <b>null in
/// <c>OnConnectedAsync</c> and <c>OnDisconnectedAsync</c></b>, which is exactly where group
/// membership is established. The filter's lifetime callbacks write <c>Context.Items</c> and nothing
/// else, so <c>Context.Items</c> is the one source that works in both places.
/// </item>
/// </list>
/// <para>
/// The filter's <c>OnConnectedAsync</c> runs before the hub's, so the value is already present by the
/// time a hub sees the connection. It is resolved through <c>IUserContextLoader</c>, which reads the
/// database. A <c>null</c> return means the connection could not be resolved to a user; a caller must
/// fail closed on it rather than treat it as "unconstrained".
/// </para>
/// </remarks>
public static class HubUserContext
{
    /// <summary>
    /// The <see cref="HubCallerContext.Items"/> key <see cref="UserContextHubFilter"/> writes the
    /// resolved <see cref="UserContext"/> under.
    /// </summary>
    /// <remarks>
    /// Public so that a hub reads the key rather than repeating the literal. A duplicated literal
    /// that drifts does not fail to compile - it silently yields <c>null</c>, and a hub failing
    /// closed on <c>null</c> would quietly stop isolating rather than break.
    /// </remarks>
    public const string ItemsKey = "__user_ctx";

    /// <summary>
    /// Returns the server-resolved user on this connection, or <c>null</c> if there is none.
    /// </summary>
    public static UserContext? GetUserContext(this HubCallerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Items.TryGetValue(ItemsKey, out var value)
            ? value as UserContext
            : null;
    }
}
