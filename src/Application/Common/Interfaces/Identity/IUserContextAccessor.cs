namespace CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;

/// <summary>
/// Interface for accessing and managing user context with support for nested contexts.
/// </summary>
/// <remarks>
/// <b>The ambient context is populated only inside a SignalR hub invocation.</b> That is the one
/// fact a caller of <see cref="Current"/> has to know before writing the line, and it is stated on
/// the property rather than only here.
/// </remarks>
public interface IUserContextAccessor
{
    /// <summary>
    /// Gets the current user context.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is <c>null</c> outside a SignalR hub method invocation, and that is most of the
    /// application.</b> The sole writer is <c>UserContextHubFilter.InvokeMethodAsync</c>, which
    /// pushes an <c>AsyncLocal</c> for the duration of one hub method call. A Blazor Server circuit
    /// runs over SignalR, so component event handlers and the Mediator handlers they dispatch are
    /// inside that window and do see a value.
    /// </para>
    /// <para>
    /// <b>Everything else does not, and needs a different source.</b> Every HTTP request - minimal
    /// API endpoints, controllers, static file paths; the cookie handler's principal validation,
    /// where <c>IdleSessionEnforcer</c> runs; background and hosted work; and the hub's own
    /// <c>OnConnectedAsync</c> / <c>OnDisconnectedAsync</c>, because the filter's lifetime callbacks
    /// write <c>HubCallerContext.Items</c> and nothing else. Two replacements, and no third:
    /// inside a hub, <c>HubUserContext.GetUserContext()</c> off the <c>HubCallerContext</c>; on an
    /// HTTP path, <c>IUserContextLoader.LoadAsync</c>, which takes a <c>ClaimsPrincipal</c>, reads
    /// the tenant from the USER ROW, caches per user and is invalidated on tenant switch. Never the
    /// principal's tenant claim - <c>ApplicationUserClaimsPrincipalFactory</c> does not add one, so
    /// it is absent for every user who has never switched tenant.
    /// </para>
    /// <para>
    /// <b>Why this warning is here and not only where it was written.</b> The same three-way
    /// analysis lives in <c>HubUserContext</c>'s remarks, which is the right place for a hub author
    /// and no place at all for anyone else: Pass 38 A2 recorded that it sat away from the API it
    /// warned about and therefore had no readership. Pass 36 and Pass 38 both cost a pass to a call
    /// site that reached for an ambient value that was null there - the file-endpoint tenant read
    /// being the expensive one, since a null tenant makes
    /// <c>VisibleDocumentSpecification</c>'s no-tenant branch drop the tenant clause entirely and
    /// serve every public document in the installation. <b>A null here reads as "unconstrained" and
    /// must be treated as "unresolved": fail closed.</b>
    /// </para>
    /// <para>
    /// No consumer list is kept here, deliberately. There are ~20 injection sites across Application
    /// and Infrastructure; an enumeration would be stale within a pass, and Pass 42 §A.4 found
    /// accumulated lists mislead precisely because absence and presence read identically in prose.
    /// </para>
    /// </remarks>
    UserContext? Current { get; }

    /// <summary>
    /// Pushes a new user context onto the stack.
    /// </summary>
    /// <param name="context">
    /// The user context to push, or <c>null</c> to mean "no principal for this scope" - which
    /// HIDES any context an outer scope pushed, rather than falling through to it. Pass 54: tenant
    /// seeding runs this way from inside an administrator's circuit, so that it behaves exactly as
    /// it does at startup, where there is no principal at all.
    /// </param>
    /// <returns>A disposable object that will pop the context when disposed.</returns>
    IDisposable Push(UserContext? context);

    void Clear();
}
