namespace CleanArchitecture.Blazor.Application.Common.Security;

/// <summary>
/// Grants a permission constant to the administrator role (Pass 60b, decision 2). For an application's own
/// constants (a module's or the application level's), so that deciding them needs no edit to
/// <see cref="AdministratorPermissionRegistry"/>'s lists, which hold the template's own decisions.
/// </summary>
/// <remarks>
/// <code>
/// public static partial class Permissions
/// {
///     public static class StockItems
///     {
///         [AdministratorGrant, Description("Allows viewing stock items")]
///         public const string View = "Permissions.StockItems.View";
///     }
/// }
/// </code>
/// An approval permission (<c>[ApprovalPermission]</c>) can never carry it: startup fails.
/// </remarks>
[AttributeUsage(AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
public sealed class AdministratorGrantAttribute : Attribute;

/// <summary>
/// Keeps a permission constant from the administrator role, with the reason (Pass 60b, decision 2). Every
/// approval permission of an application must carry it. See <see cref="AdministratorGrantAttribute"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
public sealed class AdministratorExcludedAttribute(string reason) : Attribute
{
    /// <summary>Why the administrator does not hold it. Must not be blank.</summary>
    public string Reason { get; } = reason;
}
