// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Application.Common.Security;

public static partial class Permissions
{
    /// <summary>
    /// The system log's rights, all three of which are INSTALLATION-WIDE.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The descriptions below are the boundary's only visible warning, so they state the scope.</b>
    /// <c>[Description]</c> text is what the role editor shows an administrator who is deciding
    /// whether to grant a right; it is the last point at which "this reaches every tenant" can change
    /// someone's mind. The previous wording - "Allows viewing log details" - said nothing about
    /// scope, and a reader of a multi-tenant product reasonably assumes the product's default, which
    /// everywhere else in this template is tenant-scoped.
    /// </para>
    /// <para>
    /// <b>The log is not scoped and will not be, and that is a decision.</b> The ambient tenant is
    /// populated only inside a Blazor circuit - <c>UserContextHubFilter</c> is the sole place that
    /// pushes it - so startup, seeding, every sign-in, every mail send and every HTTP-level exception
    /// are recorded with no tenant at all. A filtered view would be an edited log rather than a
    /// smaller one, a filter would also silently narrow <c>PurgeAsync</c>'s <c>ExecuteDelete</c>, and
    /// the same content reaches <c>./log/log-*.txt</c> with no gate whatever the database view does.
    /// The README's "What a Logs.View holder can see" carries the full reasoning and the operator
    /// instruction: build a customer-administrator role WITHOUT these three.
    /// </para>
    /// </remarks>
    [DisplayName("Log Permissions")]
    [Description("Set permissions for log operations - all of them installation-wide")]
    public static class Logs
    {
        [Description("Allows viewing the installation's system log - every tenant's activity, in full")]
        public const string View = "Permissions.Logs.View";

        [Description("Allows searching the installation's system log, across every tenant")]
        public const string Search = "Permissions.Logs.Search";

        // Export is deliberately absent. It existed for ExportSystemLogsQuery, which Pass 11B deleted
        // as dead code - the SystemLogs page has never had an Export button and nothing ever sent
        // that query. A permission constant nothing can check is not harmless: it appears in the role
        // editor as a grantable right, so an administrator can spend a decision on a capability the
        // application does not have. If log export is ever built, the constant comes back with it.

        // Worth its own thought when granting: this is not "clear my rows". PurgeAsync is an
        // unfiltered ExecuteDelete over the whole table, so it erases every tenant's history at
        // once, permanently, and the application offers no log export to fall back on.
        [Description("Allows permanently erasing the entire system log, for every tenant at once")]
        public const string Purge = "Permissions.Logs.Purge";
    }
}

public class LogsAccessRights
{
    public bool View { get; set; }
    public bool Search { get; set; }

    // No Export, matching Permissions.Logs above. PermissionService builds the claim string from the
    // property NAME - "Permissions.Logs." + prop.Name - so a property left here would go on
    // manufacturing a claim string that no constant declares and no role can be granted.
    public bool Purge { get; set; }
} 
