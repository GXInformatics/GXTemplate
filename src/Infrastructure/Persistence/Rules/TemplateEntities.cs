using CleanArchitecture.Blazor.Domain.Identity;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;

namespace CleanArchitecture.Blazor.Infrastructure.Persistence.Rules;

/// <summary>
/// Every entity type the template itself ships (Pass 60). These keep their tables in the default schema and
/// belong to no module; every other entity in the model must declare one with <c>[Module&lt;T&gt;]</c>.
/// </summary>
/// <remarks>
/// <b>Listed, not derived.</b> An application's entities live in the same Domain assembly as the template's,
/// so no assembly or namespace test can tell them apart without also letting an application entity placed in
/// the wrong folder pass as a template one. A table the template adds must be added here, or
/// <see cref="DataModelGuard"/> names it at startup.
/// </remarks>
public static class TemplateEntities
{
    public static readonly IReadOnlySet<Type> Types = new HashSet<Type>
    {
        typeof(ApplicationUser), typeof(ApplicationRole), typeof(ApplicationUserClaim), typeof(ApplicationRoleClaim),
        typeof(ApplicationUserLogin), typeof(ApplicationUserRole), typeof(ApplicationUserToken),
        typeof(IdentityUserPasskey<string>), typeof(Tenant), typeof(TenantUser), typeof(AuditTrail), typeof(Document),
        typeof(PicklistSet), typeof(SecurityPolicy), typeof(DataProtectionKey),
        // Pass 58: the posting-transaction infrastructure tables.
        typeof(ProcessedRequest), typeof(DocumentSequence),
        // Pass 59: the branch tables.
        typeof(Branch), typeof(UserBranch),
        // Pass 60: approval limits.
        typeof(ApprovalLimit)
    };

    /// <summary>True for a template type, and for a type derived from one (an application may extend Branch).</summary>
    public static bool Contains(Type clr) => Types.Any(t => t.IsAssignableFrom(clr));
}
