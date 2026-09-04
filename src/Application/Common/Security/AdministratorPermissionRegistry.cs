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
        "There is no email-template management page in this template - no route, no component, no " +
        "request type. Granting a permission for a feature nobody can reach advertises a capability " +
        "the application does not have. The constants stay so the permission names are reserved and " +
        "the intent is visible; grant them if and when the page is built. (The .cshtml templates " +
        "under Resources/EmailTemplates are a different thing entirely - they are the bodies of the " +
        "welcome, activation and recovery emails, and they are live.)";

    private const string ExcludedRoleSurface =
        "There is no users-in-role or claims-in-role administration in this template, and no " +
        "read-only permission viewer: the Roles page's one permission feature is the dialog " +
        "Roles.ManagePermissions already gates, and viewing happens inside it. Granting these " +
        "would advertise a role-administration surface the application does not have. The " +
        "constants stay so the names are reserved and the intent is visible; grant them if and " +
        "when the surface is built.";

    private const string ExcludedDashboard =
        "The dashboard is routed at @page \"/\" - it is the landing page every authenticated user " +
        "arrives on. Gating it would strand a principal without this right on a 403 at sign-in, " +
        "which is a worse failure than the right doing nothing. The name is reserved for when the " +
        "dashboard moves off the root route; at that point it becomes a one-line page attribute " +
        "and this entry moves back to Granted.";

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

        // Granted, and for the same reason Users.ViewAllTenants was in Pass 27: it preserves the
        // posture that already held. Before Pass 29 nothing filtered AuditTrails at all, so the
        // administrator saw every tenant's audit history. The filter now scopes it by default;
        // granting this keeps the shipped administrator seeing what they saw, while making the
        // capability named, enforced and revocable rather than an absence of code.
        Permissions.AuditTrails.ViewAllTenants,

        Permissions.Documents.View,
        Permissions.Documents.Create,
        Permissions.Documents.Edit,
        Permissions.Documents.Delete,
        Permissions.Documents.Download,
        Permissions.Documents.Search,

        // Documents.Export and Documents.Import were here until Pass 26 removed the constants:
        // neither capability exists, and the two query files they named were empty. Same reason,
        // and the same treatment, as Logs.Export below.

        Permissions.Hangfire.View,

        // Logs.Export was here until Pass 11C removed the constant: its query was deleted as dead
        // code in Pass 11B, and this list may only name constants that exist.
        Permissions.Logs.View,
        Permissions.Logs.Search,
        Permissions.Logs.Purge,

        Permissions.PicklistSets.View,
        Permissions.PicklistSets.Create,
        Permissions.PicklistSets.Edit,
        Permissions.PicklistSets.Delete,
        Permissions.PicklistSets.Search,
        Permissions.PicklistSets.Export,
        Permissions.PicklistSets.Import,

        // Granted, and for the same reason AuditTrails.ViewAllTenants was in Pass 29: it preserves
        // the posture that already held. Before Pass 32 every principal with PicklistSets.Edit could
        // change a shared value; the guard now requires this right, and granting it keeps the
        // shipped administrator able to do what it could do, while making the capability named and
        // revocable rather than an absence of code.
        //
        // It also keeps the SINGLE-TENANT deployment working out of the box, which is the case a
        // blanket read-only rule would have broken: the sole administrator is tenant-scoped, so
        // without this right the seeded picklist values would be uneditable by anyone, forever.
        Permissions.PicklistSets.ManageShared,

        Permissions.Roles.View,
        Permissions.Roles.Create,
        Permissions.Roles.Edit,
        Permissions.Roles.Delete,
        Permissions.Roles.Search,
        Permissions.Roles.Export,
        Permissions.Roles.Import,
        Permissions.Roles.ManagePermissions,

        // The five other Roles.* rights are EXCLUDED rather than granted - see below. They name a
        // role-administration surface this template does not have.

        Permissions.SecuritySettings.View,
        Permissions.SecuritySettings.Edit,

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

        // Granted, which PRESERVES what a default installation shows: the bootstrap administrator is
        // seeded into every tenant, and before Pass 27 the users grid was unfiltered, so an
        // administrator saw every tenant's users either way. What changes is that the capability is
        // now named, enforced and revocable - the same move Pass 22 made for IsActive, where a
        // posture that held by accident was replaced by one that is stated.
        Permissions.Users.ViewAllTenants,

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
            [Permissions.EmailTemplates.Delete] = ExcludedEmailTemplates,

            [Permissions.Roles.ManageUsersInRole] = ExcludedRoleSurface,
            [Permissions.Roles.ViewUsersInRole] = ExcludedRoleSurface,
            [Permissions.Roles.ManageClaimsInRole] = ExcludedRoleSurface,
            [Permissions.Roles.ViewClaimsInRole] = ExcludedRoleSurface,
            [Permissions.Roles.ViewPermissions] = ExcludedRoleSurface,

            [Permissions.Dashboards.View] = ExcludedDashboard
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
