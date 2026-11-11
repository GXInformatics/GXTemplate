using CleanArchitecture.Blazor.Domain.Common.Entities;
using CleanArchitecture.Blazor.Domain.Identity;

namespace CleanArchitecture.Blazor.Domain.Entities;

/// <summary>
/// A branch of the company (Pass 59): head office or a site. Applications add their own fields.
/// </summary>
/// <remarks>
/// <b>A branch is deactivated, never deleted.</b> An inactive branch stays readable to the users assigned
/// to it, because its history stays, but nobody can write to it. Users are assigned through
/// <see cref="UserBranch"/>; a branch row itself is not branch-filtered.
/// </remarks>
public class Branch : BaseVersionedEntity, IAuditable
{
    /// <summary>A short unique code, such as <c>HQ</c> or <c>ABJ</c>. Used in document numbers.</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Assigns a user to a branch (Pass 59). The assigned branches are the user's write set (the active ones)
/// and read set (all of them). One assignment per user may be the default, which a new session starts in.
/// </summary>
public class UserBranch
{
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser? User { get; set; }

    public int BranchId { get; set; }
    public Branch? Branch { get; set; }

    /// <summary>The branch a new session starts in. At most one per user (a filtered unique index).</summary>
    public bool IsDefault { get; set; }
}
