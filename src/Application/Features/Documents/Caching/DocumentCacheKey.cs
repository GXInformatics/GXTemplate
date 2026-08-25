// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Application.Features.Documents.Caching;

public static class DocumentCacheKey
{
    public const string GetAllCacheKey = "all-documents";
    public static string GetStreamByIdKey(int id, string? userId, string? tenantId)
    {
        // The principal is part of the key: document bytes are visibility-scoped, so two users must
        // never share a cache entry for the same document id.
        return $"GetStreamByIdKey:{id},UserId:{userId},TenantId:{tenantId}";
    }
    public static string GetPaginationCacheKey(string parameters)
    {
        return $"DocumentsWithPaginationQuery,{parameters}";
    }
    public static IEnumerable<string>? Tags => new string[] { "document" };
}
