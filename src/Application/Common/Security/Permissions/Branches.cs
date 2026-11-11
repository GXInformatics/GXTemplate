namespace CleanArchitecture.Blazor.Application.Common.Security;

public static partial class Permissions
{
    /// <summary>Branches (Pass 59). See the README, "Branches".</summary>
    [DisplayName("Branch Permissions")]
    [Description("Set permissions for branches and for reading or writing across them")]
    public static class Branches
    {
        [Description("Allows viewing the list of branches")]
        public const string View = "Permissions.Branches.View";

        [Description("Allows creating a branch")]
        public const string Create = "Permissions.Branches.Create";

        [Description("Allows editing or deactivating a branch, and assigning users to branches")]
        public const string Edit = "Permissions.Branches.Edit";

        /// <remarks>Reading only. It never grants writing to a branch the user is not assigned to.</remarks>
        [Description("Allows READING the records of every branch - never writing to them")]
        public const string ViewAll = "Permissions.Branches.ViewAll";

        /// <remarks>
        /// Amendment B1: head-office roles that approve, reverse and pay for records of any branch. Checked
        /// fresh on every branch command, and only for an active branch. ViewAll never implies it, and the
        /// bootstrap administrator is never granted it.
        /// </remarks>
        [Description("Allows WRITING to every active branch without being assigned to it")]
        public const string WriteAll = "Permissions.Branches.WriteAll";
    }
}

public class BranchesAccessRights
{
    public bool View { get; set; }
    public bool Create { get; set; }
    public bool Edit { get; set; }
    public bool ViewAll { get; set; }
    public bool WriteAll { get; set; }
}
