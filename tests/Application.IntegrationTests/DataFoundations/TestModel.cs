using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Domain.Common.Entities;
using CleanArchitecture.Blazor.Domain.Common.Precision;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CleanArchitecture.Blazor.Application.IntegrationTests.DataFoundations;

// Pass 59. The template ships no ledger, no posted document and no transfer, so these test-only
// entities stand in for them. They live in TestModelDbContext, in a database of their own.

/// <summary>A row of one branch.</summary>
public class TestBranchRow : BaseEntity, IMustHaveBranch
{
    public int BranchId { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// <summary>An inter-branch transfer: visible to both branches, and to no third.</summary>
public class TestTransfer : BaseEntity, IBranchVisibility<TestTransfer>
{
    public int FromBranchId { get; set; }
    public int ToBranchId { get; set; }
    public string Note { get; set; } = string.Empty;

    public static Expression<Func<TestTransfer, int[], bool>> VisibleTo =>
        (t, ids) => ids.Contains(t.FromBranchId) || ids.Contains(t.ToBranchId);

    public IEnumerable<int> InvolvedBranchIds => [FromBranchId, ToBranchId];
}

/// <summary>A ledger row: insert only.</summary>
public class TestLedgerRow : BaseEntity<long>, IAppendOnly
{
    public string Ref { get; set; } = string.Empty;

    [Money] public decimal Amount { get; set; }
}

/// <summary>A document header that becomes immutable when posted.</summary>
public class TestPostedDocument : BaseVersionedEntity<Guid>, IPostable
{
    public TestPostedDocument() => Id = Guid.CreateVersion7();

    public DateTime? PostedAt { get; set; }

    [AllowedAfterPosting] public string Status { get; set; } = "Draft";

    [AllowedAfterPosting] public Guid? ReversedById { get; set; }

    public string? Note { get; set; }

    public List<TestPostedLine> Lines { get; set; } = new();
}

/// <summary>A line of <see cref="TestPostedDocument"/>.</summary>
public class TestPostedLine : BaseEntity<long>, IPostableLine<TestPostedDocument>
{
    public Guid HeaderId { get; set; }
    public TestPostedDocument? Header { get; set; }

    [Quantity] public decimal Quantity { get; set; }

    [UnitCost] public decimal UnitCost { get; set; }

    /// <summary>A counter a posted line may still change (B2).</summary>
    [Quantity, AllowedAfterPosting] public decimal ReturnedQuantity { get; set; }
}

/// <summary>The application's context plus the test entities. Same options, same rules.</summary>
public class TestModelDbContext : ApplicationDbContext
{
    public TestModelDbContext(DbContextOptions<ApplicationDbContext> options, IUserContextAccessor? userContextAccessor = null)
        : base(options, userContextAccessor)
    {
    }

    public DbSet<TestBranchRow> TestBranchRows => Set<TestBranchRow>();
    public DbSet<TestTransfer> TestTransfers => Set<TestTransfer>();
    public DbSet<TestLedgerRow> TestLedgerRows => Set<TestLedgerRow>();
    public DbSet<TestPostedDocument> TestPostedDocuments => Set<TestPostedDocument>();
    public DbSet<TestPostedLine> TestPostedLines => Set<TestPostedLine>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        // BEFORE the base: the Pass 59 conventions (precision, versioning, branch filters) loop over the
        // entity types present when they run, exactly as a project's own configurations are.
        builder.Entity<TestBranchRow>();
        builder.Entity<TestTransfer>();
        builder.Entity<TestLedgerRow>();
        builder.Entity<TestPostedDocument>()
            .HasMany(d => d.Lines).WithOne(l => l.Header).HasForeignKey(l => l.HeaderId);
        builder.Entity<TestPostedLine>();
        base.OnModelCreating(builder);
    }
}
