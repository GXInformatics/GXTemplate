using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Domain.Common.Entities;
using CleanArchitecture.Blazor.Domain.Common.Modules;
using CleanArchitecture.Blazor.Domain.Common.Precision;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CleanArchitecture.Blazor.Application.IntegrationTests.DataFoundations;

// Pass 59. The template ships no ledger, no posted document and no transfer, so these test-only
// entities stand in for them. They live in TestModelDbContext, in a database of their own.
//
// (P60) Every application entity declares a module, so pass 59's test entities share one. (P60c) Its tables are in
// core, like every business table: a module no longer chooses a schema.

/// <summary>The module of pass 59's test entities.</summary>
public sealed class FoundationsTestModule : ModuleDefinition
{
    public override string Name => "FoundationsTest";
}

/// <summary>Pass 60: the first of two sample modules.</summary>
public sealed class AlphaModule : ModuleDefinition
{
    public override string Name => "Alpha";
}

/// <summary>Pass 60: the second sample module.</summary>
public sealed class BetaModule : ModuleDefinition
{
    public override string Name => "Beta";
}

/// <summary>
/// Pass 60, amendment C3: a module entity with NO DbSet property anywhere. It enters the model only through
/// its configuration, and is reached only through <c>Set&lt;AlphaThing&gt;()</c>. Branch-scoped and with a
/// decimal kind, so the filter, the branch grant and the precision rule all apply to it.
/// </summary>
[Module<AlphaModule>]
public class AlphaThing : BaseEntity, IMustHaveBranch
{
    public int BranchId { get; set; }
    public string Name { get; set; } = string.Empty;

    [Quantity] public decimal OnHand { get; set; }
}

/// <summary>Pass 60: a row of module Beta referring to module Alpha by id, with a foreign key and no navigation.</summary>
[Module<BetaModule>]
public class BetaThing : BaseEntity
{
    public int AlphaThingId { get; set; }
    public string Note { get; set; } = string.Empty;
}

/// <summary>
/// Pass 60, amendment C4: a balance-like row a posting creates by its natural key (item, branch) while another
/// transaction may be creating the same one. Identity key, unique natural key, branch-scoped, a quantity.
/// </summary>
[Module<FoundationsTestModule>]
public class TestBalance : BaseEntity<long>, IMustHaveBranch
{
    public string ItemCode { get; set; } = string.Empty;
    public int BranchId { get; set; }

    [Quantity] public decimal OnHand { get; set; }
}

/// <summary>Pass 60, amendment C4: a tenant-scoped row, which InsertIfAbsent refuses.</summary>
[Module<FoundationsTestModule>]
public class TestTenantRow : BaseEntity, IMustHaveTenant
{
    public string TenantId { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
}

/// <summary>Pass 60, amendment C4: audit fields (IAuditableEntity) without the audit trail (not IAuditable).</summary>
[Module<FoundationsTestModule>]
public class TestStampedRow : BaseAuditableEntity
{
    public string Code { get; set; } = string.Empty;
}

/// <summary>Pass 60, Part A: a branch-scoped document that needs approval.</summary>
[Module<FoundationsTestModule>]
public class TestApprovableDocument : BaseVersionedEntity<long>, IApprovable<TestApprovableDocument>, IMustHaveBranch, IAuditable
{
    public static string ApprovalDocumentType => "test.document";

    public int BranchId { get; set; }
    public string? PreparedById { get; set; }
    [Money] public decimal ApprovalValue { get; set; }
    public string? ApprovedById { get; set; }
    public DateTime? ApprovedAt { get; set; }
}

/// <summary>Pass 60, amendment C1: an approvable document with NO branch scope.</summary>
[Module<FoundationsTestModule>]
public class TestPlainApprovable : BaseVersionedEntity<long>, IApprovable<TestPlainApprovable>
{
    public static string ApprovalDocumentType => "test.plain";

    public string? PreparedById { get; set; }
    [Money] public decimal ApprovalValue { get; set; }
    public string? ApprovedById { get; set; }
    public DateTime? ApprovedAt { get; set; }
}

/// <summary>The configurations a module would ship in Infrastructure/Modules/&lt;M&gt;/Configurations.</summary>
public sealed class AlphaThingConfiguration : IEntityTypeConfiguration<AlphaThing>
{
    public void Configure(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<AlphaThing> builder) =>
        builder.Property(t => t.Name).HasMaxLength(100);
}

public sealed class BetaThingConfiguration : IEntityTypeConfiguration<BetaThing>
{
    public void Configure(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<BetaThing> builder) =>
        builder.HasOne<AlphaThing>().WithMany().HasForeignKey(t => t.AlphaThingId).OnDelete(DeleteBehavior.Restrict);
}

/// <summary>A row of one branch.</summary>
[Module<FoundationsTestModule>]
public class TestBranchRow : BaseEntity, IMustHaveBranch
{
    public int BranchId { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// <summary>An inter-branch transfer: visible to both branches, and to no third.</summary>
[Module<FoundationsTestModule>]
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
[Module<FoundationsTestModule>]
public class TestLedgerRow : BaseEntity<long>, IAppendOnly
{
    public string Ref { get; set; } = string.Empty;

    [Money] public decimal Amount { get; set; }
}

/// <summary>A document header that becomes immutable when posted.</summary>
[Module<FoundationsTestModule>]
public class TestPostedDocument : BaseVersionedEntity<long>, IPostable
{
    public DateTime? PostedAt { get; set; }

    [AllowedAfterPosting] public string Status { get; set; } = "Draft";

    [AllowedAfterPosting] public long? ReversedById { get; set; }

    public string? Note { get; set; }

    public List<TestPostedLine> Lines { get; set; } = new();
}

/// <summary>A line of <see cref="TestPostedDocument"/>.</summary>
[Module<FoundationsTestModule>]
public class TestPostedLine : BaseEntity<long>, IPostableLine<TestPostedDocument>
{
    public long HeaderId { get; set; }
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

        // Pass 60: module entities come in through their configurations, as a module's do.
        builder.ApplyConfiguration(new AlphaThingConfiguration());
        builder.ApplyConfiguration(new BetaThingConfiguration());
        builder.Entity<TestBalance>().HasIndex(b => new { b.ItemCode, b.BranchId }).IsUnique();
        builder.Entity<TestTenantRow>().HasIndex(t => t.Code).IsUnique();
        builder.Entity<TestStampedRow>().HasIndex(t => t.Code).IsUnique();
        builder.Entity<TestApprovableDocument>();
        builder.Entity<TestPlainApprovable>();
        base.OnModelCreating(builder);
    }
}
