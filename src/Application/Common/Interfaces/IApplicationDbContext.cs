// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using CleanArchitecture.Blazor.Domain.Identity;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace CleanArchitecture.Blazor.Application.Common.Interfaces;

public interface IApplicationDbContext: IAsyncDisposable
{
    // SystemLog is deliberately absent: logs live in their own database behind ILogDbContext, so
    // that no query written against the business context can join across the two.
    DbSet<AuditTrail> AuditTrails { get; set; }
    DbSet<Document> Documents { get; set; }
    DbSet<PicklistSet> PicklistSets { get; set; }

    /// <summary>The administered security policy - one row. See <c>SecurityPolicy</c>.</summary>
    DbSet<SecurityPolicy> SecurityPolicies { get; set; }
    DbSet<Tenant> Tenants { get; set; }
    DbSet<TenantUser> TenantUsers { get; set; }

    /// <summary>Branches (Pass 59). Not branch-filtered: a branch row is reference data.</summary>
    DbSet<Branch> Branches { get; set; }

    /// <summary>User-to-branch assignments (Pass 59). Written only by <c>SetUserBranchesCommand</c>.</summary>
    DbSet<UserBranch> UserBranches { get; set; }

    /// <summary>Approval limits (Pass 60). Written only by the approval-limit commands.</summary>
    DbSet<ApprovalLimit> ApprovalLimits { get; set; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The change-tracking entry for <paramref name="entity"/> (Pass 59). Implemented by <c>DbContext.Entry</c>
    /// itself; exposed so that <c>ApplyLoadedVersion</c> can set a concurrency token's original value
    /// without casting to the concrete context.
    /// </summary>
    Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<TEntity> Entry<TEntity>(TEntity entity) where TEntity : class;

    /// <summary>
    /// The set of any entity in the model (Pass 60). Implemented by <c>DbContext.Set</c> itself.
    /// </summary>
    /// <remarks>
    /// <b>How a module reaches its entities (amendment C3).</b> A module entity is never a DbSet property here or
    /// on <c>ApplicationDbContext</c> - architecture rule 10 fails if one is - so that a module never edits the
    /// shared context. Its configuration brings it into the model; handlers write <c>db.Set&lt;StockItem&gt;()</c>.
    /// Every query filter, save rule and guard applies to it exactly as to a DbSet property: they iterate the
    /// model, not the properties.
    /// </remarks>
    DbSet<TEntity> Set<TEntity>() where TEntity : class;

    /// <summary>
    /// The context's database facade, so a handler can own a transaction -
    /// <c>await using var tx = await db.Database.BeginTransactionAsync(ct);</c> - without casting to
    /// the concrete context (Pass 55). Implemented by <c>DbContext.Database</c> itself.
    /// </summary>
    /// <remarks>
    /// <c>AuditableEntityInterceptor</c> opens a transaction only when none is current, so audit
    /// rows join the handler's transaction and roll back with it. A handler that needs a document
    /// header and its lines - or a stock movement and its balance - to commit together is the reason
    /// this exists. It is the transaction surface, not an invitation to raw SQL from Application.
    /// </remarks>
    DatabaseFacade Database { get; }
}
