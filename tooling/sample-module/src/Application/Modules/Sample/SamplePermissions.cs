namespace CleanArchitecture.Blazor.Application.Common.Security;

// The sample module's permissions: a nested class of the partial Permissions, decided by attribute (pass 60b, decision 2),
// so no template list is edited.
public static partial class Permissions
{
    [DisplayName("Sample Bins")]
    [Description("The smoke test's sample module")]
    public static class SampleBins
    {
        [AdministratorGrant]
        [Description("Allows viewing sample bins")]
        public const string View = "Permissions.SampleBins.View";

        [AdministratorExcluded("Bins are created in a branch by its own staff; the administrator writes only where assigned.")]
        [Description("Allows creating sample bins")]
        public const string Create = "Permissions.SampleBins.Create";
    }
}
