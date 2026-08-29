// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Application.Common.Interfaces;

/// <summary>
/// Read access to the log database, plus the one destructive operation the SystemLogs page offers.
/// </summary>
/// <remarks>
/// The shape of this interface is the enforcement mechanism for two rules that would otherwise be
/// conventions somebody has to remember.
/// <para>
/// <b>Nothing in the business layer can write here.</b> <see cref="SystemLogs"/> is an
/// <see cref="IQueryable{T}"/>, not a <c>DbSet&lt;SystemLog&gt;</c>, so <c>Add</c>, <c>Update</c>,
/// <c>Remove</c> and <c>SaveChanges</c> are not on the surface at all - a caller cannot write a
/// mutation for the compiler to accept. Serilog's sink owns writing; this side only reads.
/// </para>
/// <para>
/// <b>No query can accidentally join across databases.</b> <c>SystemLog</c> is not in
/// <c>ApplicationDbContext</c>'s model and the business entities are not in <c>LogDbContext</c>'s,
/// so there is no LINQ expression spanning the two for EF to translate into SQL that would fail
/// against a database holding one table.
/// </para>
/// <para>
/// The purge is a named capability rather than a consequence of handing out a writable set, because
/// erasing the log is a thing the application genuinely does - the Clear Logs button, behind
/// <c>Permissions.Logs.Purge</c> - and it should be visible in the abstraction that permits it.
/// </para>
/// </remarks>
public interface ILogDbContext : IAsyncDisposable
{
    /// <summary>The log rows, untracked. Query only.</summary>
    IQueryable<SystemLog> SystemLogs { get; }

    /// <summary>Deletes every log row. The only write this interface permits.</summary>
    /// <returns>The number of rows deleted.</returns>
    Task<int> PurgeAsync(CancellationToken cancellationToken = default);
}
