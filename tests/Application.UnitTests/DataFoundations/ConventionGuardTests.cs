#nullable enable
using System;
using System.Collections.Generic;
using CleanArchitecture.Blazor.Application.Common.Transactions;
using CleanArchitecture.Blazor.Domain.Common;
using CleanArchitecture.Blazor.Domain.Common.Entities;
using CleanArchitecture.Blazor.Domain.Common.Modules;
using CleanArchitecture.Blazor.Domain.Common.Precision;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Rules;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.DataFoundations;

// Pass 60c plan §7 rows 9, 14 and 15, and §3.11: the GX-convention rules of DataModelGuard (lookups, keys, enums) and
// the posting-event id check. Each scenario is its own context type (EF caches one model per context type); no
// database is touched.

[Module<GuardModule>] public class ConventionColour : BaseLkEntity { public string? Code { get; set; } public int SortOrder { get; set; } }
[Module<GuardModule>] public class ConventionUnit : BaseVersionedLkEntity { public bool IsActive { get; set; } }
[Module<GuardModule>] public class ConventionRateLookup : BaseLkEntity { [Rate] public decimal Rate { get; set; } }
[Module<GuardModule>] public class ConventionNamelessLookup : BaseLkEntity { }
[Module<GuardModule>] public class ConventionLongLookup : BaseEntity<long>, ILookupEntity
{
    int ILookupEntity.Id => (int)Id;
    public string Name { get; set; } = "";
    public string? Slug { get; set; }
}

[Module<GuardModule>] public class ConventionGuidKeyed : BaseEntity<Guid> { }
[Module<GuardModule>] public class ConventionStringKeyed : BaseEntity<string> { }
[Module<GuardModule>] public class ConventionIntLedger : BaseEntity<int>, IAppendOnly { [Money] public decimal Amount { get; set; } }
[Module<GuardModule>] public class ConventionLongLedger : BaseEntity<long>, IAppendOnly { [Money] public decimal Amount { get; set; } }
[Module<GuardModule>] public class ConventionCompositeKeyed : BaseEntity { public int Second { get; set; } }

public enum ConventionImplicitStatus { Draft, Posted }
[ExplicitValues] public enum ConventionExplicitStatus { Draft = 1, Posted = 2 }
[Module<GuardModule>] public class ConventionEnumHolder : BaseEntity
{
    public ConventionImplicitStatus Implicit { get; set; }
    public ConventionExplicitStatus Explicit { get; set; }
    public ConventionImplicitStatus? NullableImplicit { get; set; }
}

public sealed class GoodLookupsContext() : GuardContext(Options()) { protected override void Add(ModelBuilder b) { b.Entity<ConventionColour>(); b.Entity<ConventionUnit>(); } }
public sealed class RateLookupContext() : GuardContext(Options()) { protected override void Add(ModelBuilder b) => b.Entity<ConventionRateLookup>(); }
public sealed class NamelessLookupContext() : GuardContext(Options()) { protected override void Add(ModelBuilder b) => b.Entity<ConventionNamelessLookup>().Ignore(l => l.Name); }
public sealed class LongLookupContext() : GuardContext(Options()) { protected override void Add(ModelBuilder b) => b.Entity<ConventionLongLookup>(); }
public sealed class HandNamedLookupContext() : GuardContext(Options()) { protected override void Add(ModelBuilder b) => b.Entity<ConventionColour>().ToTable("colours", "core"); }
public sealed class KeysContext() : GuardContext(Options())
{
    protected override void Add(ModelBuilder b)
    {
        b.Entity<ConventionGuidKeyed>(); b.Entity<ConventionStringKeyed>(); b.Entity<ConventionIntLedger>(); b.Entity<ConventionLongLedger>();
        b.Entity<ConventionCompositeKeyed>().HasKey(x => new { x.Id, x.Second });
    }
}
public sealed class EnumsContext() : GuardContext(Options()) { protected override void Add(ModelBuilder b) => b.Entity<ConventionEnumHolder>(); }

public sealed class EarlyIdEvent(int warehouseId) : PostingEvent
{
    public int WarehouseId { get; } = warehouseId;
}

public sealed class LateIdEvent(ConventionColour colour) : PostingEvent
{
    private readonly ConventionColour _colour = colour;
    public int ColourId => _colour.Id;
    public long? OptionalId { get; init; }
}

public class ConventionGuardTests
{
    private static IReadOnlyList<string> ProblemsOf(DbContext context)
    {
        using (context) return DataModelGuard.FindProblems(context.Model);
    }

    // ---- Lookups (D4, Q-A2) --------------------------------------------------------------------

    [Test]
    public void ALookupWithTheFixedColumns_AndFurtherOnes_IsAccepted_InCore_AsTblLkPlural()
    {
        using var context = new GoodLookupsContext();
        DataModelGuard.FindProblems(context.Model).Should().BeEmpty();
        var colour = context.Model.FindEntityType(typeof(ConventionColour))!;
        (colour.GetSchema(), colour.GetTableName()).Should().Be(("core", "TBL_LK_CONVENTION_COLOURS"));
        colour.FindProperty(nameof(ConventionColour.Name))!.IsNullable.Should().BeFalse("Name is required");
        context.Model.FindEntityType(typeof(ConventionUnit))!.GetTableName().Should().Be("TBL_LK_CONVENTION_UNITS");
    }

    [Test]
    public void ALookupWithADecimalColumn_FailsStartup()
    {
        ProblemsOf(new RateLookupContext()).Should().ContainSingle()
            .Which.Should().StartWith("ConventionRateLookup.Rate:").And.Contain("decision Q-A2").And.Contain("master data");
    }

    [Test]
    public void ALookupWhoseNameIsNotMapped_FailsStartup()
    {
        ProblemsOf(new NamelessLookupContext()).Should().ContainSingle()
            .Which.Should().StartWith("ConventionNamelessLookup.Name:").And.Contain("fixed column");
    }

    [Test]
    public void ALookupWithALongKey_FailsStartup()
    {
        ProblemsOf(new LongLookupContext()).Should().Contain(p => p.StartsWith("ConventionLongLookup:") && p.Contains("one int column Id"));
    }

    [Test]
    public void ALookupNamedByHandOutOfTheTblLkConvention_FailsStartup()
    {
        ProblemsOf(new HandNamedLookupContext()).Should().ContainSingle()
            .Which.Should().StartWith("ConventionColour:").And.Contain("TBL_LK_").And.Contain("\"colours\"");
    }

    // ---- Keys (D6, Q-I) ------------------------------------------------------------------------

    [Test]
    public void GuidStringAndCompositeKeys_FailStartup_AndALedgerNeedsALongKey()
    {
        var problems = ProblemsOf(new KeysContext());
        problems.Should().Contain(p => p.StartsWith("ConventionGuidKeyed:") && p.Contains("Guid key") && p.Contains("Never a Guid"));
        problems.Should().Contain(p => p.StartsWith("ConventionStringKeyed:") && p.Contains("String key"));
        problems.Should().Contain(p => p.StartsWith("ConventionCompositeKeyed:") && p.Contains("composite key"));
        problems.Should().Contain(p => p.StartsWith("ConventionIntLedger:") && p.Contains("IAppendOnly") && p.Contains("long"));
        problems.Should().NotContain(p => p.StartsWith("ConventionLongLedger:"));
        problems.Should().HaveCount(4);
    }

    // ---- Enums (Q-H) ---------------------------------------------------------------------------

    [Test]
    public void AnEnumWithoutExplicitValues_FailsStartup_NullableOrNot()
    {
        var problems = ProblemsOf(new EnumsContext());
        problems.Should().HaveCount(2);
        problems.Should().Contain(p => p.StartsWith("ConventionEnumHolder.Implicit:") && p.Contains("[ExplicitValues]"));
        problems.Should().Contain(p => p.StartsWith("ConventionEnumHolder.NullableImplicit:"));
        problems.Should().NotContain(p => p.StartsWith("ConventionEnumHolder.Explicit:"));
    }

    // ---- The posting-event id (D6, Q-J) --------------------------------------------------------

    [Test]
    public void APostingEventThatCapturedAZeroId_IsRefused_NamingEventAndProperty()
    {
        FluentActions.Invoking(() => PostingEventIdCheck.Assert(new EarlyIdEvent(0)))
            .Should().Throw<InvalidOperationException>().WithMessage("EarlyIdEvent.WarehouseId is 0*BranchCreatedEvent pattern*");
        FluentActions.Invoking(() => PostingEventIdCheck.Assert(new EarlyIdEvent(7))).Should().NotThrow();
    }

    [Test]
    public void APostingEventThatReadsTheIdWhenAsked_PassesOnceTheRowIsSaved_AndANullableIdIsSkipped()
    {
        var colour = new ConventionColour { Name = "Red" };
        var raised = new LateIdEvent(colour);

        // Before the save the id is 0, and the event says so if it were dispatched then...
        FluentActions.Invoking(() => PostingEventIdCheck.Assert(raised)).Should().Throw<InvalidOperationException>();

        // ...but it is dispatched after the save, when EF has assigned the identity value.
        colour.Id = 42;
        FluentActions.Invoking(() => PostingEventIdCheck.Assert(raised)).Should().NotThrow("OptionalId is nullable and null");
    }
}
