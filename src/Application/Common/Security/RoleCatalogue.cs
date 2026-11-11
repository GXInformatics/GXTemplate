using CleanArchitecture.Blazor.Application.Common.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace CleanArchitecture.Blazor.Application.Common.Security;

/// <summary>A role an application declares, with its description and permissions (Pass 60b, item 3).</summary>
/// <param name="DeclaredBy">The module's name, or <c>AppLevel</c>.</param>
public sealed record ApplicationRoleDefinition(string Name, string Description, IReadOnlyList<string> Permissions, string DeclaredBy);

/// <summary>Permissions an application grants to a role declared elsewhere (Pass 60b, item 3).</summary>
public sealed record ApplicationRoleGrant(string Role, IReadOnlyList<string> Permissions, string DeclaredBy);

/// <summary>One role as the initializer reconciles it: declared roles have a description; <c>Basic</c> has none.</summary>
public sealed record CatalogueRole(string Name, string? Description, IReadOnlyList<string> Permissions);

/// <summary>
/// The application's role catalogue (Pass 60b, item 3): roles and grants declared through the module hook, which
/// <c>ApplicationDbContextInitializer.ProvisionAsync</c> reconciles on every start. It grants what is missing and
/// never revokes, as for the template's own roles.
/// </summary>
/// <remarks>
/// <code>
/// // in a ModuleServices or AppLevelServices Register method
/// services.AddApplicationRole("Storekeeper", "Receives and issues stock in assigned branches.",
///         Permissions.StockItems.View, Permissions.StockMovements.Post)
///     .GrantToRole("Branch Manager", Permissions.StockItems.View);   // a role another module declares
/// </code>
/// <para>
/// <b>One declaration per role</b>, with its description; other modules grant to it. The administrator role is never
/// named (decision 7): its grant is <see cref="AdministratorPermissionRegistry"/>. <c>Basic</c> may receive grants.
/// </para>
/// </remarks>
public static class RoleCatalogue
{
    /// <summary>Identity's role-name column length.</summary>
    public const int MaxNameLength = 256;

    public static IModuleServiceCollection AddApplicationRole(this IModuleServiceCollection services, string name,
        string description, params string[] permissions)
    {
        services.AddSingleton(new ApplicationRoleDefinition(name, description, permissions, services.Owner));
        return services;
    }

    public static IModuleServiceCollection GrantToRole(this IModuleServiceCollection services, string role, params string[] permissions)
    {
        services.AddSingleton(new ApplicationRoleGrant(role, permissions, services.Owner));
        return services;
    }

    /// <summary>
    /// Checks the catalogue and merges grants into their roles. Role names compare case-insensitively, as Identity
    /// normalises them.
    /// </summary>
    /// <param name="knownPermissions">Every <c>Permissions.*</c> constant.</param>
    /// <exception cref="InvalidOperationException">Every problem found, each naming the role and the declaring module.</exception>
    public static IReadOnlyList<CatalogueRole> Resolve(IEnumerable<ApplicationRoleDefinition> definitions,
        IEnumerable<ApplicationRoleGrant> grants, IReadOnlyCollection<string> knownPermissions)
    {
        var defs = definitions.ToList();
        var grantList = grants.ToList();
        var known = new HashSet<string>(knownPermissions, StringComparer.Ordinal);
        var problems = new List<string>();
        var comparer = StringComparer.OrdinalIgnoreCase;

        foreach (var def in defs)
        {
            problems.AddRange(NameProblems(def.Name, def.DeclaredBy));
            if (comparer.Equals(def.Name, Roles.Basic))
                problems.Add($"{def.DeclaredBy} declares role '{def.Name}', which the template declares. Grant to it with GrantToRole instead.");
            if (string.IsNullOrWhiteSpace(def.Description))
                problems.Add($"{def.DeclaredBy} declares role '{def.Name}' with no description.");
        }

        foreach (var twice in defs.GroupBy(d => d.Name, comparer).Where(g => g.Count() > 1))
            problems.Add($"Role '{twice.Key}' is declared {twice.Count()} times (by {string.Join(", ", twice.Select(d => d.DeclaredBy))}). " +
                         "One module declares a role; the others grant to it with GrantToRole.");

        var declared = new HashSet<string>(defs.Select(d => d.Name).Append(Roles.Basic), comparer);
        foreach (var grant in grantList)
        {
            problems.AddRange(NameProblems(grant.Role, grant.DeclaredBy));
            if (!comparer.Equals(grant.Role, Roles.Admin) && !declared.Contains(grant.Role))
                problems.Add($"{grant.DeclaredBy} grants to role '{grant.Role}', which no module declares. Declare it with AddApplicationRole.");
        }

        foreach (var (permission, role, by) in defs.SelectMany(d => d.Permissions.Select(p => (p, d.Name, d.DeclaredBy)))
                     .Concat(grantList.SelectMany(g => g.Permissions.Select(p => (p, g.Role, g.DeclaredBy)))))
        {
            if (!known.Contains(permission))
                problems.Add($"{by} gives role '{role}' the permission '{permission}', which is not a Permissions constant.");
        }

        if (problems.Count > 0)
            throw new InvalidOperationException("Role catalogue (README, \"Roles\"):\n- " + string.Join("\n- ", problems.Distinct()));

        return defs.Select(d => (d.Name, (string?)d.Description))
            .Append((Roles.Basic, null))
            .Select(r => new CatalogueRole(r.Item1, r.Item2,
                defs.Where(d => comparer.Equals(d.Name, r.Item1)).SelectMany(d => d.Permissions)
                    .Concat(grantList.Where(g => comparer.Equals(g.Role, r.Item1)).SelectMany(g => g.Permissions))
                    .Distinct(StringComparer.Ordinal).ToList()))
            .Where(r => r.Description is not null || r.Permissions.Count > 0)
            .ToList();
    }

    private static IEnumerable<string> NameProblems(string name, string declaredBy)
    {
        if (string.IsNullOrWhiteSpace(name))
            yield return $"{declaredBy} names a role with no name.";
        else if (name.Length > MaxNameLength)
            yield return $"{declaredBy} names role '{name[..40]}…', longer than {MaxNameLength} characters.";
        else if (StringComparer.OrdinalIgnoreCase.Equals(name, Roles.Admin))
            yield return $"{declaredBy} names the administrator role '{name}'. The administrator's grant is AdministratorPermissionRegistry " +
                         "and is never extended by the catalogue (decision 7).";
    }
}
