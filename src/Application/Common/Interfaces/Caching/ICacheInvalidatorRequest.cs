// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;

/// <summary>
/// A request that invalidates cached responses after it succeeds.
/// <para>
/// <b>Invalidation is deliberately NOT scoped.</b> Every cacheable request in a feature carries
/// that feature's tag, and every invalidator flushes the same tag; a tag flush removes matching
/// entries whatever key they were stored under, so it already reaches every scoped variant. Folding
/// the acting principal into an invalidator would be actively wrong - it would flush only that
/// user's copies and leave everyone else reading data the command just changed.
/// </para>
/// <para>
/// <c>CacheKey</c> keeps its empty default here, and it means something: an invalidator that flushes
/// by tag alone has no single key to name. The behaviour skips the key removal when it is empty.
/// </para>
/// </summary>
public interface ICacheInvalidatorRequest<TResponse> : IRequest<TResponse>
{
    /// <summary>An unscoped key to remove outright, or empty to rely on <see cref="Tags"/> alone.</summary>
    string CacheKey => string.Empty;

    IEnumerable<string>? Tags { get; }
}
