// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;

/// <summary>
/// A request whose response the pipeline may cache.
/// <para>
/// Both members are deliberately abstract. <c>CacheKey</c> used to default to
/// <c>string.Empty</c>, so a request that forgot to declare one still compiled and cached itself -
/// and every other forgetful request - under the single key "". Requiring both makes that
/// unwritable: a cacheable request that does not say what identifies its entry, and whose data it
/// belongs to, does not build.
/// </para>
/// </summary>
public interface ICacheableRequest<TResponse> : IRequest<TResponse>
{
    /// <summary>Identifies the entry within its scope. Must include every parameter the response varies by.</summary>
    string CacheKey { get; }

    /// <summary>
    /// Whose data this is. The pipeline folds the matching identity components into the effective
    /// key - see <see cref="CacheScope"/> for why declaring it is not the same as getting it right.
    /// </summary>
    CacheScope Scope { get; }

    IEnumerable<string>? Tags { get; }
}
