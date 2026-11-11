namespace CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;

/// <summary>
/// Represents the current user context with essential user information.
/// </summary>
/// <param name="TenantId">The tenant this principal is currently acting in, if any.</param>
/// <param name="AllowedTenantIds">
/// Every tenant this principal may see: the union of their <c>TenantUsers</c> membership rows and
/// <see cref="TenantId"/>. <c>UserContextLoader</c> is what computes it.
/// <para>
/// <b><c>null</c> and empty mean different things and a consumer must not conflate them.</b>
/// <c>null</c> means nobody computed this - the context was built some other way, by a test double
/// or by a caller constructing the record directly - and a scoping decision cannot be made from it.
/// An empty list means it WAS computed and the principal genuinely belongs to no tenant, which is a
/// representable state. Treating <c>null</c> as empty would turn "unknown" into "denied
/// everything"; treating empty as <c>null</c> would turn "belongs to nothing" into "unconstrained".
/// </para>
/// </param>
public sealed record UserContext(
    string UserId,
    string UserName,
    string? DisplayName = null,
    string? TenantId = null,
    IReadOnlyList<string>? AllowedTenantIds = null,
    string? Email = null,
    IReadOnlyList<string>? Roles = null,
    string? ProfilePictureDataUrl = null,
    string? SuperiorId = null,
    string? IpAddress = null,
    BranchAccess? Branches = null
);

/// <summary>
/// What a principal may do across branches (Pass 59), computed by <c>UserContextLoader</c>.
/// </summary>
/// <param name="ReadsAll">Holds <c>Permissions.Branches.ViewAll</c>: the branch filter shows every branch. Never grants writing.</param>
/// <param name="ReadIds">The assigned branches, active or not (history stays readable).</param>
/// <param name="WriteIds">The assigned ACTIVE branches. View-all never adds to these.</param>
/// <param name="WritesAll">
/// The system context, or a holder of <c>Permissions.Branches.WriteAll</c> (amendment B1). For a person it
/// is informational - the switcher offers every active branch - and the write check itself re-reads the
/// permission on each send.
/// </param>
/// <param name="DefaultBranchId">The branch a new session starts in, if any.</param>
/// <param name="IsSystem">The system account: reads and writes every branch without a command grant.</param>
/// <remarks>
/// <b>A null <see cref="UserContext.Branches"/> means "not computed" and is treated as no access</b>: the
/// branch filter returns no rows, as the must-have tenant filter does with no principal.
/// </remarks>
public sealed record BranchAccess(
    bool ReadsAll,
    IReadOnlyList<int> ReadIds,
    IReadOnlyList<int> WriteIds,
    bool WritesAll,
    int? DefaultBranchId,
    bool IsSystem = false)
{
    /// <summary>Background work under <c>ISystemContext</c>: every branch, read and write.</summary>
    public static BranchAccess System { get; } = new(true, [], [], true, null, true);
}
