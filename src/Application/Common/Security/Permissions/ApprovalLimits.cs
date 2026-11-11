namespace CleanArchitecture.Blazor.Application.Common.Security;

public static partial class Permissions
{
    /// <summary>Approval limits (Pass 60). See the README, "Approvals".</summary>
    [DisplayName("Approval Limit Permissions")]
    [Description("Set permissions for viewing and maintaining approval limits")]
    public static class ApprovalLimits
    {
        [Description("Allows viewing the approval limits")]
        public const string View = "Permissions.ApprovalLimits.View";

        /// <remarks>
        /// Setting a limit is not approving. The administrator holds this (owner decision 10) and still never approves:
        /// the approval policy refuses any member of the administrator role.
        /// </remarks>
        [Description("Allows adding, changing and deactivating approval limits")]
        public const string Edit = "Permissions.ApprovalLimits.Edit";
    }
}

public class ApprovalLimitsAccessRights
{
    public bool View { get; set; }
    public bool Edit { get; set; }
}
