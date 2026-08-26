// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Application.Features.Documents.Specifications;

/// <summary>
/// The documents a given principal is allowed to see: their own private ones plus every public one,
/// and only inside their own tenant.
/// </summary>
/// <remarks>
/// Extracted so there is exactly ONE definition of document visibility. Two things enforce it now -
/// <c>GetFileStreamQueryHandler</c> for the download button, and the <c>/files</c> streaming
/// endpoint for anything rendered straight from a document's PublicUrl - and a security rule with
/// two copies is a security rule with one copy that is out of date.
/// </remarks>
public class VisibleDocumentSpecification : Specification<Document>
{
    public VisibleDocumentSpecification(string userId, string tenantId)
    {
        Query.Where(p => (p.CreatedById == userId && p.IsPublic == false) || p.IsPublic == true)
            .Where(x => x.TenantId == tenantId, !string.IsNullOrEmpty(tenantId));
    }
}
