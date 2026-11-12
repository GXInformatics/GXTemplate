#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.Numerics;
using CleanArchitecture.Blazor.Domain.Common.Entities;
using CleanArchitecture.Blazor.Domain.Common.Modules;
using CleanArchitecture.Blazor.Domain.Common.Precision;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Rules;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.DataFoundations;

// Pass 59 plan §5 rows 1, 2, 3, 18, 25 and 5. Each scenario is its own context type, because EF caches
// one model per context type. No database is touched: building a model needs only the provider.

// (P60) Every entity in an application model now declares a module; these test entities share one, so each
// scenario still reports only the problem it was built for.
public sealed class GuardModule : ModuleDefinition
{
    public override string Name => "Guard";
}

[Module<GuardModule>]
public class GuardUndeclared : BaseEntity { public decimal Price { get; set; } }

[Module<GuardModule>]
public class GuardKinds : BaseEntity
{
    [Money] public decimal Amount { get; set; }
    [Quantity] public decimal Quantity { get; set; }
    [UnitCost] public decimal UnitCost { get; set; }
    [Rate] public decimal? TaxRate { get; set; }
}

[Module<GuardModule>]
public class TwoKinds : BaseEntity { [Money, Rate] public decimal Both { get; set; } }
[Module<GuardModule>]
public class KindOnString : BaseEntity { [Money] public string? Text { get; set; } }
[Module<GuardModule>]
public class GuardFloats : BaseEntity { public double Weight { get; set; } [Approximate] public double Latitude { get; set; } }

[Module<GuardModule>]
public class SoftLedger : BaseEntity, IAppendOnly, ISoftDelete
{
    public DateTime? DeletedAt { get; set; }
    public string? DeletedById { get; set; }
}

[Module<GuardModule>]
public class UnversionedPostable : BaseEntity, IPostable
{
    public DateTime? PostedAt { get; set; }
    [AllowedAfterPosting] public DateTime? Unused { get; set; }
}

[Module<GuardModule>]
public class LedgerAndDocument : BaseVersionedEntity, IAppendOnly, IPostable { public DateTime? PostedAt { get; set; } }

[Module<GuardModule>]
public class StrayAllowed : BaseEntity { [AllowedAfterPosting] public string? Status { get; set; } }

[Module<GuardModule>]
public class PostedAtAllowed : BaseVersionedEntity, IPostable { [AllowedAfterPosting] public DateTime? PostedAt { get; set; } }

[Module<GuardModule>]
public class UnfilteredBranchRow : BaseEntity, IMustHaveBranch { public int BranchId { get; set; } }

[Module<GuardModule>]
public class TphRoot : BaseEntity { public string? Kind { get; set; } }
public class TphBranchLeaf : TphRoot, IMustHaveBranch { public int BranchId { get; set; } }

[Module<GuardModule>]
public class GuardProbeWarehouse : BaseEntity { public int BranchId { get; set; } }
[Module<GuardModule>]
public class NavigatingTransfer : BaseEntity, IBranchVisibility<NavigatingTransfer>
{
    public int WarehouseId { get; set; }
    public GuardProbeWarehouse? Warehouse { get; set; }
    public static Expression<Func<NavigatingTransfer, int[], bool>> VisibleTo => (t, ids) => ids.Contains(t.Warehouse!.BranchId);
    public IEnumerable<int> InvolvedBranchIds => [];
}

public abstract class GuardContext(DbContextOptions<ApplicationDbContext> options) : ApplicationDbContext(options)
{
    protected abstract void Add(ModelBuilder builder);

    protected virtual void After(ModelBuilder builder) { }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        Add(builder);
        base.OnModelCreating(builder);
        After(builder);
    }

    public static DbContextOptions<ApplicationDbContext> Options() =>
        new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql("Host=model-only").Options;
}

public sealed class UndeclaredContext() : GuardContext(Options()) { protected override void Add(ModelBuilder b) => b.Entity<GuardUndeclared>(); }
public sealed class KindsContext() : GuardContext(Options()) { protected override void Add(ModelBuilder b) => b.Entity<GuardKinds>(); }
public sealed class OverriddenKindContext() : GuardContext(Options())
{
    protected override void Add(ModelBuilder b) => b.Entity<GuardKinds>();
    protected override void After(ModelBuilder b) => b.Entity<GuardKinds>().Property(k => k.Amount).HasPrecision(10, 3);
}
public sealed class TwoKindsContext() : GuardContext(Options()) { protected override void Add(ModelBuilder b) { b.Entity<TwoKinds>(); b.Entity<KindOnString>(); } }
public sealed class FloatsContext() : GuardContext(Options()) { protected override void Add(ModelBuilder b) => b.Entity<GuardFloats>(); }
public sealed class ImmutabilityContext() : GuardContext(Options())
{
    protected override void Add(ModelBuilder b)
    {
        b.Entity<SoftLedger>(); b.Entity<UnversionedPostable>(); b.Entity<LedgerAndDocument>();
        b.Entity<StrayAllowed>(); b.Entity<PostedAtAllowed>();
    }
}
public sealed class UnfilteredBranchContext() : GuardContext(Options())
{
    protected override void Add(ModelBuilder b) => b.Entity<UnfilteredBranchRow>();
    // Something after the convention removes the named filter - what the guard exists to notice.
    protected override void After(ModelBuilder b) => b.Entity<UnfilteredBranchRow>().HasQueryFilter(QueryFilters.Branch, null);
}
public sealed class TphBranchContext() : GuardContext(Options()) { protected override void Add(ModelBuilder b) { b.Entity<TphRoot>(); b.Entity<TphBranchLeaf>(); } }
public sealed class NavigatingTransferContext() : GuardContext(Options()) { protected override void Add(ModelBuilder b) { b.Entity<GuardProbeWarehouse>(); b.Entity<NavigatingTransfer>(); } }

public class DataModelGuardTests
{
    private static IReadOnlyList<string> ProblemsOf(DbContext context)
    {
        using (context) return DataModelGuard.FindProblems(context.Model);
    }

    [Test]
    public void TheTemplatesOwnModelIsSound()
    {
        using var context = new ApplicationDbContext(GuardContext.Options());
        DataModelGuard.FindProblems(context.Model).Should().BeEmpty();
        FluentActions.Invoking(() => DataModelGuard.AssertModel(context.Model)).Should().NotThrow();
    }

    [Test]
    public void T01_ADecimalWithNoDeclaredKindFailsStartup()
    {
        using var context = new UndeclaredContext();
        DataModelGuard.FindProblems(context.Model).Should().ContainSingle().Which.Should().Contain("GuardUndeclared.Price").And.Contain("no declared kind");
        FluentActions.Invoking(() => DataModelGuard.AssertModel(context.Model)).Should().Throw<InvalidOperationException>()
            .WithMessage("*GuardUndeclared.Price*");
    }

    [Test]
    public void T02_EachKindMapsToItsPrecision_AndAnOverrideOrAMisplacedKindFails()
    {
        using (var context = new KindsContext())
        {
            var type = context.Model.FindEntityType(typeof(GuardKinds))!;
            (int?, int?) Of(string name) => (type.FindProperty(name)!.GetPrecision(), type.FindProperty(name)!.GetScale());
            Of(nameof(GuardKinds.Amount)).Should().Be((18, 2));
            Of(nameof(GuardKinds.Quantity)).Should().Be((18, 4));
            Of(nameof(GuardKinds.UnitCost)).Should().Be((18, 4));
            Of(nameof(GuardKinds.TaxRate)).Should().Be((18, 6));
            DataModelGuard.FindProblems(context.Model).Should().BeEmpty();
        }

        ProblemsOf(new OverriddenKindContext()).Should().ContainSingle().Which.Should().Contain("GuardKinds.Amount").And.Contain("numeric(10,3)");
        var problems = ProblemsOf(new TwoKindsContext());
        problems.Should().Contain(p => p.Contains("TwoKinds.Both") && p.Contains("more than one kind"));
        problems.Should().Contain(p => p.Contains("KindOnString.Text") && p.Contains("decimal only"));
    }

    [Test]
    public void T03_ADoubleFailsUnlessMarkedApproximate()
    {
        ProblemsOf(new FloatsContext()).Should().ContainSingle().Which.Should().Contain("GuardFloats.Weight").And.Contain("[Approximate]");
    }

    [Test]
    public void T18_ImmutabilityMarkersThatContradictFailStartup()
    {
        var problems = ProblemsOf(new ImmutabilityContext());
        problems.Should().Contain(p => p.StartsWith("SoftLedger:") && p.Contains("ISoftDelete"));
        problems.Should().Contain(p => p.StartsWith("UnversionedPostable:") && p.Contains("not IVersioned"));
        problems.Should().Contain(p => p.StartsWith("LedgerAndDocument:") && p.Contains("both IAppendOnly and IPostable"));
        problems.Should().Contain(p => p.StartsWith("StrayAllowed.Status") && p.Contains("neither IPostable nor IPostableLine"));
        problems.Should().Contain(p => p.StartsWith("PostedAtAllowed.PostedAt") && p.Contains("can never change"));
    }

    [Test]
    public void T25_ABranchScopedEntityWithoutTheBranchFilterFailsStartup_AsDoALeafMarkerAndANavigatingRule()
    {
        ProblemsOf(new UnfilteredBranchContext()).Should().ContainSingle()
            .Which.Should().Contain("UnfilteredBranchRow").And.Contain($"'{QueryFilters.Branch}' query filter");
        ProblemsOf(new TphBranchContext()).Should().ContainSingle()
            .Which.Should().Contain("TphBranchLeaf").And.Contain("Put the marker on TphRoot");
        ProblemsOf(new NavigatingTransferContext()).Should().ContainSingle()
            .Which.Should().Contain("NavigatingTransfer.VisibleTo").And.Contain("'Warehouse'");
    }

    [Test]
    public void T05_GxRoundRoundsMidpointsAwayFromZero_AtEachKindsScale()
    {
        GxRound.Money(2.345m).Should().Be(2.35m);
        GxRound.Money(-2.345m).Should().Be(-2.35m);
        GxRound.Money(2.344m).Should().Be(2.34m);
        GxRound.Quantity(1.00005m).Should().Be(1.0001m);
        GxRound.Quantity(-1.00005m).Should().Be(-1.0001m);
        GxRound.UnitCost(0.12345m).Should().Be(0.1235m);
        GxRound.Rate(0.0000005m).Should().Be(0.000001m);
        GxRound.Rate(-0.0000005m).Should().Be(-0.000001m);

        GxRound.SignificantScale(1.50m).Should().Be(1, "trailing zeros do not count");
        GxRound.SignificantScale(1.005m).Should().Be(3);
        GxRound.SignificantScale(100m).Should().Be(0);
    }
}
