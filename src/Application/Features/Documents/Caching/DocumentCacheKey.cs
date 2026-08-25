// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Application.Features.Documents.Caching;

public static class DocumentCacheKey
{
    public const string GetAllCacheKey = "all-documents";
    public static string GetStreamByIdKey(int id)
    {
        // The principal is no longer spelled into the key by hand: GetFileStreamQuery declares
        // CacheScope.PerUserAndTenant and the caching behaviour folds the ambient user and tenant
        // in. Two principals still cannot share an entry - it is now the pipeline that guarantees
        // it rather than each query remembering to.
        return $"GetStreamByIdKey:{id}";
    }
    public static string GetPaginationCacheKey(string parameters)
    {
        return $"DocumentsWithPaginationQuery,{parameters}";
    }
    public static IEnumerable<string>? Tags => new string[] { "document" };
}
