// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Linq.Expressions;

namespace CleanArchitecture.Blazor.Application.Features.Documents.Specifications;

/// <summary>
/// The documents a given principal is allowed to see: their own private ones plus every public one,
/// and only inside their own tenant.
/// </summary>
/// <remarks>
/// Extracted so there is exactly ONE definition of document visibility. A security rule with two
/// copies is a security rule with one copy that is out of date.
/// <para>
/// <b>Its consumers as of Pass 43, listed to orient a reader and not as a bound on how many there
/// may be:</b> <c>GetFileStreamQueryHandler</c> for the download button, the <c>/files</c> streaming
/// endpoint for anything rendered straight from a document's PublicUrl,
/// <c>AdvancedDocumentsSpecification</c> for every listing, and the edit and delete commands before
/// they touch a row. Nothing enumerates them and no test holds the list to those five, so read it as
/// "at least these" and confirm with a find-usages rather than trusting the count - Pass 38 added the
/// <c>/files</c> endpoint and this paragraph had to be edited by hand to say so.
/// </para>
/// <para>
/// <b>What the list is for is the rule beside it, which does not change when the list does:</b> a new
/// consumer applies <see cref="IsVisibleTo"/> rather than restating the clause. That is the whole
/// reason the rule is a shared expression, and it is why a sixth consumer missing from the paragraph
/// above is a stale comment and not a security defect - whereas a sixth consumer that wrote the
/// clause out by hand would be the defect, and would contradict nothing here.
/// </para>
/// <para>
/// <b>The rule lives in <see cref="IsVisibleTo"/>, not in this constructor.</b> The specification is
/// a thin wrapper over it, so callers that already have a <c>Specification&lt;Document&gt;</c> of
/// their own - the paginated listing, which also has list views and a keyword - can apply the same
/// expression without inheriting from this type or restating it. Pass 24 found the listing had
/// restated it, in two of the four list views it had at the time and not the other two.
/// </para>
/// </remarks>
public class VisibleDocumentSpecification : Specification<Document>
{
    public VisibleDocumentSpecification(string userId, string tenantId)
    {
        Query.Where(IsVisibleTo(userId, tenantId));
    }

    /// <summary>
    /// Whether a document is visible to the given principal: their own private ones plus every
    /// public one, confined to their tenant.
    /// </summary>
    /// <remarks>
    /// <b>The tenant clause is conditional on the caller HAVING a tenant</b>, which is the behaviour
    /// this rule has always had and is deliberately not changed here: a principal with no tenant is
    /// confined by ownership and publicity alone. Narrowing that is a scoping decision and belongs
    /// with the rest of the isolation work, not in a repair.
    /// <para>
    /// Written as two whole expressions rather than one composed conditionally, because a
    /// <c>Where</c> that is sometimes absent is exactly how the listing lost the clause in the first
    /// place. Both are complete statements of the rule.
    /// </para>
    /// </remarks>
    public static Expression<Func<Document, bool>> IsVisibleTo(string? userId, string? tenantId) =>
        string.IsNullOrEmpty(tenantId)
            ? p => (p.CreatedById == userId && p.IsPublic == false) || p.IsPublic == true
            : p => ((p.CreatedById == userId && p.IsPublic == false) || p.IsPublic == true)
                   && p.TenantId == tenantId;
}
