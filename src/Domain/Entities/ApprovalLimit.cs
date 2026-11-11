using CleanArchitecture.Blazor.Domain.Common.Entities;
using CleanArchitecture.Blazor.Domain.Common.Precision;

namespace CleanArchitecture.Blazor.Domain.Entities;

/// <summary>
/// How much a role may approve of one document type, optionally in one branch (Pass 60).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>A user's limit is the HIGHEST over their roles' rows for the document type.</description></item>
/// <item><description>For a branch-scoped document, a role's row for the document's branch overrides that role's
/// row with no branch. For a document with no branch scope only rows with no branch count (amendment C1).</description></item>
/// <item><description>No row means no authority: the approval is refused.</description></item>
/// <item><description>The administrator role can hold no row; the administrator never approves.</description></item>
/// </list>
/// A template table, in the default schema. Versioned and audited: every change to a limit has an author.
/// </remarks>
public class ApprovalLimit : BaseVersionedEntity, IAuditable
{
    /// <summary>The role the limit belongs to.</summary>
    public string RoleId { get; set; } = string.Empty;

    /// <summary>The document type, an <c>IApprovable.ApprovalDocumentType</c> code such as <c>"pur.order"</c>.</summary>
    public string DocumentType { get; set; } = string.Empty;

    /// <summary>The branch the row applies to, or null for every branch.</summary>
    public int? BranchId { get; set; }

    /// <summary>The highest value the role may approve.</summary>
    [Money] public decimal MaxValue { get; set; }

    /// <summary>An inactive row grants nothing; rows are deactivated, not deleted, so their history stays.</summary>
    public bool IsActive { get; set; } = true;
}
