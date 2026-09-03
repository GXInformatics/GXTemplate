// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Linq.Expressions;

namespace CleanArchitecture.Blazor.Application.Features.Documents.Specifications;

/// <summary>
/// The documents a given principal is allowed to see: their own private ones plus every public one,
/// and only inside their own tenant.
/// </summary>
/// <remarks>
/// Extracted so there is exactly ONE definition of document visibility. It is enforced now by
/// <c>GetFileStreamQueryHandler</c> for the download button, by the <c>/files</c> streaming endpoint
/// for anything rendered straight from a document's PublicUrl, by
/// <c>AdvancedDocumentsSpecification</c> for every listing, and by the edit and delete commands
/// before they touch a row. A security rule with two copies is a security rule with one copy that is
/// out of date.
/// <para>
/// <b>The rule lives in <see cref="IsVisibleTo"/>, not in this constructor.</b> The specification is
/// a thin wrapper over it, so callers that already have a <c>Specification&lt;Document&gt;</c> of
/// their own - the paginated listing, which also has list views and a keyword - can apply the same
/// expression without inheriting from this type or restating it. Pass 24 found the listing had
/// restated it, in two of its four list views and not the other two.
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
