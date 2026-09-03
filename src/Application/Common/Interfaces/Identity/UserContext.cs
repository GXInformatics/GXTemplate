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
    string? IpAddress = null
); 
