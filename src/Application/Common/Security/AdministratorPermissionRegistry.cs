using System.Reflection;

namespace CleanArchitecture.Blazor.Application.Common.Security;

/// <summary>
/// Startup-time enforcement of what the administrator role holds.
/// <para>
/// The seeder used to grant the administrator every <c>Permissions.*</c> constant it could find by
/// reflection. That is convenient and wrong in one specific way: adding a constant silently granted
/// it, so nobody ever had to decide. This registry replaces the loop with two explicit lists and an
/// assertion that every discovered constant appears in exactly one of them - so a new constant fails
/// the test run and the application's startup until someone consciously grants or excludes it.
/// </para>
/// <para>
/// Divergence is caught in BOTH directions. A constant in neither list fails (someone added a
/// permission and did not decide). A constant in both fails (the decision contradicts itself). A
/// listed name that is no longer a declared constant fails too (a permission was deleted and the
/// list was not updated), which is the case the reflection loop could never have detected.
/// </para>
/// <para>
/// The logic is static so it can be tested against a controlled constant list rather than only
/// through a running host.
/// </para>
/// </summary>
public static class AdministratorPermissionRegistry
{
    private const string ExcludedEmailTemplates =
        "The email-template management page does not exist in this template - only four orphaned " +
        "localisation .resx files under Resources/Pages/SystemManagement survive it. Granting a " +
        "permission for a page nobody can reach advertises a capability the application does not " +
        "have. Grant these if and when the page is built.";

    /// <summary>
    /// Every permission granted to the administrator role at provisioning time.
    /// <para>
    /// The administrator is the superuser, so the default answer is "grant". Some of these gate
    /// nothing server-side today and only drive UI affordances through the <c>*AccessRights</c>
    /// models - <c>Documents.Search</c> shows the search box, for instance. They are granted anyway:
    /// withholding a claim whose <c>AccessRights</c> property already exists would hide a control
    /// the moment someone wires it up, which is a worse failure than holding a claim nothing checks
    /// yet. The unenforced-server-side gap itself is tracked separately (Pass 4C anomaly A3) and is
    /// not what this list is for.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> Granted =
    [
        Permissions.AuditTrails.View,
        Permissions.AuditTrails.Search,
        Permissions.AuditTrails.Export,

        Permissions.Dashboards.View,

        Permissions.Documents.View,
        Permissions.Documents.Create,
        Permissions.Documents.Edit,
        Permissions.Documents.Delete,
        Permissions.Documents.Download,
        Permissions.Documents.Search,
        Permissions.Documents.Export,
        Permissions.Documents.Import,

        Permissions.Hangfire.View,

        Permissions.Logs.View,
        Permissions.Logs.Search,
        Permissions.Logs.Export,
        Permissions.Logs.Purge,

        Permissions.NavigationMenu.View,

        Permissions.PicklistSets.View,
        Permissions.PicklistSets.Create,
        Permissions.PicklistSets.Edit,
        Permissions.PicklistSets.Delete,
        Permissions.PicklistSets.Search,
        Permissions.PicklistSets.Export,
        Permissions.PicklistSets.Import,

        Permissions.Roles.View,
        Permissions.Roles.Create,
        Permissions.Roles.Edit,
        Permissions.Roles.Delete,
        Permissions.Roles.Search,
        Permissions.Roles.Export,
        Permissions.Roles.Import,
        Permissions.Roles.ManagePermissions,
        Permissions.Roles.ViewPermissions,
        Permissions.Roles.ManageUsersInRole,
        Permissions.Roles.ViewUsersInRole,
        Permissions.Roles.ManageClaimsInRole,
        Permissions.Roles.ViewClaimsInRole,

        Permissions.Tenants.View,
        Permissions.Tenants.Create,
        Permissions.Tenants.Edit,
        Permissions.Tenants.Delete,
        Permissions.Tenants.Search,

        Permissions.Users.View,
        Permissions.Users.Create,
        Permissions.Users.Edit,
        Permissions.Users.Delete,
        Permissions.Users.Search,
        Permissions.Users.Export,
        Permissions.Users.Import,
        Permissions.Users.Deactivation,
        Permissions.Users.ManagePermissions,
        Permissions.Users.ManageRoles,
        Permissions.Users.RestPassword,
        Permissions.Users.SendRestPasswordMail,
        Permissions.Users.SuppressLoginNotification,
        Permissions.Users.SwitchTenants,
        Permissions.Users.SwitchToAnyTenant,
        Permissions.Users.ViewOnlineStatus
    ];

    /// <summary>
    /// Permissions deliberately NOT granted to the administrator, each with the reason.
    /// <para>
    /// An entry here states that the constant should exist but nobody should hold it, which in
    /// practice means the feature it names is not in this template.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Excluded =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Permissions.EmailTemplates.View] = ExcludedEmailTemplates,
            [Permissions.EmailTemplates.Create] = ExcludedEmailTemplates,
            [Permissions.EmailTemplates.Edit] = ExcludedEmailTemplates,
            [Permissions.EmailTemplates.Delete] = ExcludedEmailTemplates
        };

    /// <summary>
    /// Every <c>Permissions.*</c> constant declared in the assembly, discovered the same way the
    /// rest of the authorization stack discovers them: the public static string fields of every type
    /// nested under <see cref="Permissions"/>.
    /// </summary>
    public static IReadOnlyList<string> DiscoverAllPermissions()
    {
        return typeof(Permissions).GetNestedTypes()
            .SelectMany(module => module.GetFields(
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
            .Where(field => field.FieldType == typeof(string))
            .Select(field => (string?)field.GetValue(null))
            .Where(value => !string.IsNullOrEmpty(value))
            .Select(value => value!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Throws unless every discovered permission appears in exactly one of <see cref="Granted"/> and
    /// <see cref="Excluded"/>, and every listed permission is a real constant.
    /// </summary>
    /// <exception cref="InvalidOperationException">The lists and the constants have diverged.</exception>
    public static void AssertNoDivergence()
    {
        Validate(DiscoverAllPermissions(), Granted, (IReadOnlyCollection<string>)Excluded.Keys);
    }

    /// <summary>
    /// The assertion against explicit inputs, so tests can plant a divergence without touching the
    /// real <see cref="Permissions"/> tree or the real lists. <see cref="AssertNoDivergence()"/> is
    /// this method applied to them.
    /// </summary>
    public static void Validate(
        IReadOnlyCollection<string> allPermissions,
        IReadOnlyCollection<string> grantedPermissions,
        IReadOnlyCollection<string> excludedPermissions)
    {
        if (allPermissions.Count == 0)
        {
            throw new InvalidOperationException(
                "Administrator permission registry discovered no permission constants. That means " +
                "the reflection has stopped matching (a moved namespace, a changed shape), and a " +
                "registry that finds nothing would pass forever while checking nothing.");
        }

        var granted = new HashSet<string>(grantedPermissions, StringComparer.Ordinal);
        var excluded = new HashSet<string>(excludedPermissions, StringComparer.Ordinal);
        var known = new HashSet<string>(allPermissions, StringComparer.Ordinal);

        var unlisted = allPermissions
            .Where(p => !granted.Contains(p) && !excluded.Contains(p))
            .OrderBy(p => p, StringComparer.Ordinal).ToList();
        var inBoth = allPermissions
            .Where(p => granted.Contains(p) && excluded.Contains(p))
            .OrderBy(p => p, StringComparer.Ordinal).ToList();
        var phantom = granted.Concat(excluded)
            .Where(p => !known.Contains(p))
            .OrderBy(p => p, StringComparer.Ordinal).ToList();
        var duplicated = grantedPermissions.GroupBy(p => p, StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key)
            .OrderBy(p => p, StringComparer.Ordinal).ToList();

        if (unlisted.Count == 0 && inBoth.Count == 0 && phantom.Count == 0 && duplicated.Count == 0)
        {
            return;
        }

        var message = new List<string>
        {
            $"{nameof(AdministratorPermissionRegistry)} has diverged from the {nameof(Permissions)} constants."
        };

        if (unlisted.Count > 0)
        {
            message.Add(
                $"Neither granted nor excluded - decide, then add to {nameof(Granted)} or " +
                $"{nameof(Excluded)}: {string.Join(", ", unlisted)}.");
        }

        if (inBoth.Count > 0)
        {
            message.Add($"Listed as BOTH granted and excluded: {string.Join(", ", inBoth)}.");
        }

        if (phantom.Count > 0)
        {
            message.Add(
                $"Listed but no longer a declared constant - remove from the list: {string.Join(", ", phantom)}.");
        }

        if (duplicated.Count > 0)
        {
            message.Add($"Listed more than once in {nameof(Granted)}: {string.Join(", ", duplicated)}.");
        }

        throw new InvalidOperationException(string.Join(" ", message));
    }
}
