// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using CleanArchitecture.Blazor.Application.Common.ExceptionHandlers;

namespace CleanArchitecture.Blazor.Infrastructure.Persistence.Logging;

/// <summary>
/// Mirrors <see cref="ApplicationDbContextFactory"/>, with one addition: it knows whether a log
/// database is configured at all, and refuses to invent one when it is not.
/// </summary>
internal sealed class LogDbContextFactory(
    IDbContextFactory<LogDbContext> efFactory,
    DatabaseSettings databaseSettings) : ILogDbContextFactory
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(databaseSettings.LogConnectionString);

    public ValueTask<ILogDbContext> CreateAsync(CancellationToken ct = default)
    {
        // Throwing beats returning an empty context. An unconfigured log database and an empty log
        // database are different facts, and a caller that cannot tell them apart will present the
        // first as the second - reporting a quiet week when the truth is that nothing was ever
        // being recorded.
        if (!IsConfigured) throw new LogDatabaseNotConfiguredException();

        var dbContext = efFactory.CreateDbContext();
        return new ValueTask<ILogDbContext>(dbContext);
    }
}
