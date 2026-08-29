// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Application.Common.Security;

public static partial class Permissions
{
    [DisplayName("Log Permissions")]
    [Description("Set permissions for log operations")]
    public static class Logs
    {
        [Description("Allows viewing log details")]
        public const string View = "Permissions.Logs.View";

        [Description("Allows searching for log records")]
        public const string Search = "Permissions.Logs.Search";

        // Export is deliberately absent. It existed for ExportSystemLogsQuery, which Pass 11B deleted
        // as dead code - the SystemLogs page has never had an Export button and nothing ever sent
        // that query. A permission constant nothing can check is not harmless: it appears in the role
        // editor as a grantable right, so an administrator can spend a decision on a capability the
        // application does not have. If log export is ever built, the constant comes back with it.

        [Description("Allows purging log records")]
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
