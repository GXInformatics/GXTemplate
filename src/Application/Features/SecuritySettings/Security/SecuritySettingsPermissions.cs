// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Application.Common.Security;

public static partial class Permissions
{
    [DisplayName("Security Settings Permissions")]
    [Description("Set permissions for the installation's security policy")]
    public static class SecuritySettings
    {
        [Description("Allows viewing the security policy")]
        public const string View = "Permissions.SecuritySettings.View";

        // Deliberately its own permission rather than a general administration right: changing how
        // long a session may sit unattended is a security control, and the set of people who should
        // hold it is not the same as the set who administer users or picklists.
        [Description("Allows changing the security policy, including the idle timeout")]
        public const string Edit = "Permissions.SecuritySettings.Edit";
    }
}

public class SecuritySettingsAccessRights
{
    public bool View { get; set; }
    public bool Edit { get; set; }
}
