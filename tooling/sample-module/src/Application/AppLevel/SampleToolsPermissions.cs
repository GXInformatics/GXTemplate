namespace CleanArchitecture.Blazor.Application.Common.Security;

// Amendment E2: an application-level permission, outside any module, decided by attribute.
public static partial class Permissions
{
    [DisplayName("Sample Tools")]
    [Description("The smoke test's application-level tools page")]
    public static class SampleTools
    {
        [AdministratorExcluded("A development tool; granted to the Bin Keeper role by the application level.")]
        [Description("Allows using the sample tools page")]
        public const string Use = "Permissions.SampleTools.Use";
    }
}
