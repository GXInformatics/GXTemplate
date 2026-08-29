// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Application.Common.Interfaces;

/// <summary>
/// Creates <see cref="ILogDbContext"/> instances, following the
/// <see cref="IApplicationDbContextFactory"/> idiom so the two read the same way at call sites.
/// </summary>
public interface ILogDbContextFactory
{
    /// <summary>
    /// Whether a log database is configured at all.
    /// </summary>
    /// <remarks>
    /// Callers that can render a "not configured" state cheaply should ask first rather than
    /// provoking <see cref="LogDatabaseNotConfiguredException"/> and catching it.
    /// </remarks>
    bool IsConfigured { get; }

    /// <summary>Creates a context over the log database.</summary>
    /// <exception cref="LogDatabaseNotConfiguredException">
    /// No log connection string is configured. This is a supported state, not a defect: the
    /// application runs without a log database, and callers are expected to say so rather than
    /// present an empty list as though the log were merely quiet.
    /// </exception>
    ValueTask<ILogDbContext> CreateAsync(CancellationToken ct = default);
}
