using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Domain.Common.Entities;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CleanArchitecture.Blazor.Infrastructure.Persistence;

/// <summary>
/// Startup-time proof that every tenant-marked entity is actually scoped by the tenant filter.
/// </summary>
/// <remarks>
/// <b>Why a guard as well as the convention.</b> <c>ApplicationDbContext.ApplyTenantFilters</c>
/// registers the filter by marker, so in the ordinary case this finds nothing. What it catches are
/// the ways the two can come apart without anything failing:
/// <list type="bullet">
/// <item><description>a marker on a TPH LEAF whose root is unmarked - EF only filters roots, so the
/// leaf's rows would be readable by every tenant;</description></item>
/// <item><description>a configuration or a later line in <c>OnModelCreating</c> that removes or
/// replaces the named filter;</description></item>
/// <item><description>the convention itself being edited or deleted.</description></item>
/// </list>
/// Each of those would otherwise surface as a cross-tenant read in production. It runs from
/// <c>ApplicationDbContextInitializer.InitialiseAsync</c>, before migrations, so a model that
/// fails it never serves a request. The same posture as <c>RequestAuthorizationRegistry</c>:
/// a security omission fails the start, not the first user who hits it.
/// <para>
/// It checks presence, not wording. A filter named <see cref="QueryFilters.Tenant"/> that says
/// something else would pass; the behaviour of the shipped predicates is pinned by tests.
/// </para>
/// </remarks>
public static class TenantFilterGuard
{
    /// <summary>
    /// Entities that must carry the tenant filter: every type implementing
    /// <see cref="IMustHaveTenant"/> or <see cref="IMayHaveTenant"/>, plus <see cref="AuditTrail"/>,
    /// which carries a tenant without a marker (see <c>ApplicationDbContext.OnModelCreating</c>).
    /// </summary>
    public static bool RequiresTenantFilter(Type clrType) =>
        typeof(IMustHaveTenant).IsAssignableFrom(clrType)
        || typeof(IMayHaveTenant).IsAssignableFrom(clrType)
        || clrType == typeof(AuditTrail);

    /// <summary>
    /// The entity types in <paramref name="model"/> that require the tenant filter and are not under
    /// one - by name, with the reason. Empty when the model is sound.
    /// </summary>
    public static IReadOnlyList<string> FindUnfiltered(IReadOnlyModel model)
    {
        var problems = new List<string>();
        foreach (var entityType in model.GetEntityTypes())
        {
            // An owned type cannot carry a filter of its own; it is read through its owner.
            if (entityType.IsOwned() || !RequiresTenantFilter(entityType.ClrType)) continue;

            var root = entityType.GetRootType();
            if (root.FindDeclaredQueryFilter(QueryFilters.Tenant) is not null) continue;

            problems.Add(ReferenceEquals(root, entityType)
                ? $"{entityType.ClrType.Name}: carries a tenant marker but has no '{QueryFilters.Tenant}' query filter."
                : $"{entityType.ClrType.Name}: carries a tenant marker, but EF filters only hierarchy roots and its root " +
                  $"{root.ClrType.Name} has no '{QueryFilters.Tenant}' query filter. Put the marker on {root.ClrType.Name}.");
        }
        return problems;
    }

    /// <summary>Throws, naming every offender, unless <see cref="FindUnfiltered"/> is empty.</summary>
    /// <exception cref="InvalidOperationException">A tenant-marked entity is not tenant-filtered.</exception>
    public static void AssertEveryTenantEntityIsFiltered(IReadOnlyModel model)
    {
        var problems = FindUnfiltered(model);
        if (problems.Count == 0) return;

        throw new InvalidOperationException(
            $"Tenant isolation check failed: {problems.Count} tenant-scoped entity type(s) would be readable by every tenant." +
            Environment.NewLine + string.Join(Environment.NewLine, problems.Select(p => "  - " + p)) + Environment.NewLine +
            "The filter is registered by ApplicationDbContext.ApplyTenantFilters for every IMustHaveTenant/IMayHaveTenant " +
            "hierarchy root; something has removed or bypassed it. The application refuses to start rather than serve " +
            "these rows across tenants.");
    }
}
