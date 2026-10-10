namespace CleanArchitecture.Blazor.Application.Common.Interfaces.MultiTenant;

/// <summary>
/// Brings one tenant's own data up to what the application needs - a default warehouse, a number
/// sequence, a starter set of codes. Implement it and register it with
/// <c>services.AddScoped&lt;ITenantSeeder, MySeeder&gt;()</c>; the template invokes every registered
/// seeder, in registration order, through <see cref="ITenantSeedRunner"/>.
/// </summary>
/// <remarks>
/// <b>Invoked at two points, so it must be idempotent per item.</b> Once for a tenant when it is
/// created, and again for EVERY tenant on every start, from
/// <c>ApplicationDbContextInitializer.ProvisionAsync</c>. The second call is what delivers a row a
/// later release adds to every tenant that already exists - so look each item up by its natural key
/// and add it only if it is missing. Grant-only, never revoke: a row an operator changed or added
/// is theirs, and a seeder that "tidies" would undo it on the next restart. This is the same
/// contract the role-grant reconciliation already follows.
/// <para>
/// <b>It runs with NO ambient user</b>, both at startup and when a tenant is created from an
/// administrator's circuit (the runner hides the administrator). Two consequences:
/// </para>
/// <list type="bullet">
/// <item><description><b>Set <c>TenantId</c> explicitly</b> to <c>tenantId</c> on every row you add.
/// Stamping only fills a null tenant and there is no ambient one to fill it with, so an
/// <c>IMustHaveTenant</c> row left null is refused before it is written.</description></item>
/// <item><description><b>Read with the tenant filter lifted and the tenant stated</b>:
/// <c>db.X.IgnoreQueryFilters([QueryFilters.Tenant]).Where(x =&gt; x.TenantId == tenantId)</c>.
/// With no principal the filter scopes reads to installation-level rows only, so an unlifted
/// "is it already there?" check would always say no and the seeder would duplicate on every
/// start.</description></item>
/// </list>
/// <para>
/// Do not dispatch Mediator requests from a seeder: with no principal, <c>AuthorizationBehaviour</c>
/// refuses them. Write through <c>IApplicationDbContextFactory</c>.
/// </para>
/// </remarks>
public interface ITenantSeeder
{
    Task SeedAsync(string tenantId, CancellationToken cancellationToken);
}

/// <summary>
/// Runs every registered <see cref="ITenantSeeder"/> for one tenant, with the ambient principal
/// hidden. The single entry point both invocation sites use, so they cannot drift apart.
/// </summary>
public interface ITenantSeedRunner
{
    Task SeedAsync(string tenantId, CancellationToken cancellationToken = default);
}
