namespace CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;

/// <summary>
/// Runs work as the system account (<see cref="Constants.Users.System"/>) inside one tenant: the
/// way a hosted service or a Hangfire job gets an ambient <see cref="UserContext"/>, so that it can
/// dispatch Mediator requests and read tenant-filtered sets.
/// </summary>
/// <remarks>
/// <b>Why it exists.</b> <see cref="IUserContextAccessor.Current"/> is populated only inside a
/// SignalR hub invocation. Background work has no principal, so <c>AuthorizationBehaviour</c>
/// refuses every request it sends and the tenant filter scopes its reads to installation-level rows.
/// <para>
/// <b>A callback, not an <c>IDisposable</c> scope, and that is load-bearing.</b> The context lives in
/// an <c>AsyncLocal</c>, and a value set inside an <c>async</c> method is discarded when that method
/// returns. A <c>BeginAsync()</c> that looked the account up and then pushed would hand back a scope
/// that had already ended. Pushing here and awaiting <paramref name="work"/> inside the push is
/// what keeps the work inside it.
/// </para>
/// <para>
/// <b>Authorization still applies.</b> The system account is a real user, and requests sent from
/// <paramref name="work"/> are authorized against the permissions it holds - provisioned to match
/// the administrator registry - exactly as a person's would be. Its writes are stamped and audited
/// with its user id, so "who did this" reads "gx-system" rather than nobody.
/// </para>
/// <para>
/// <b>Fails closed.</b> An empty or unknown tenant, or a missing system account (provisioning has
/// not run), throws <see cref="InvalidOperationException"/> before <paramref name="work"/> starts.
/// </para>
/// </remarks>
public interface ISystemContext
{
    Task RunAsync(string tenantId, Func<CancellationToken, Task> work, CancellationToken cancellationToken = default);

    Task<T> RunAsync<T>(string tenantId, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default);
}
