#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using CleanArchitecture.Blazor.Domain.Common.Entities;
using CleanArchitecture.Blazor.Domain.Common.Modules;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Rules;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.DataFoundations;

// Pass 60 plan §5 rows 15 and 20: the module rules of DataModelGuard, each scenario its own context type
// (EF caches one model per context type). No database is touched.

public sealed class GuardAlphaModule : ModuleDefinition
{
    public override string Name => "GuardAlpha";
}

public sealed class GuardBetaModule : ModuleDefinition
{
    public override string Name => "GuardBeta";
}

public class GuardModuleless : BaseEntity { public string? Name { get; set; } }

[Module<GuardAlphaModule>] public class AlphaRow : BaseEntity { public string? Name { get; set; } }
[Module<GuardBetaModule>] public class BetaRow : BaseEntity { public string? Name { get; set; } }

[Module<GuardAlphaModule>] public class AlphaShape : BaseEntity { public string? Kind { get; set; } }
[Module<GuardBetaModule>] public class BetaCircle : AlphaShape { public int Radius { get; set; } }

[Module<GuardAlphaModule>] public class AlphaTwin : BaseEntity { public string? Name { get; set; } }
[Module<GuardBetaModule>] public class BetaTwin : BaseEntity { public string? Name { get; set; } }

[Module<GuardBetaModule>]
public class BetaOrder : BaseEntity
{
    public int AlphaRowId { get; set; }
    public AlphaRow? AlphaRow { get; set; }

    public int BranchId { get; set; }
    public Branch? Branch { get; set; }
}

[Module<GuardBetaModule>]
public class BetaInvoice : BaseEntity
{
    // The sanctioned cross-module reference: the id, with a foreign key and no navigation.
    public int AlphaRowId { get; set; }
}

public sealed class ModulelessContext() : GuardContext(Options()) { protected override void Add(ModelBuilder b) => b.Entity<GuardModuleless>(); }
public sealed class HandNamedIntoPublicContext() : GuardContext(Options()) { protected override void Add(ModelBuilder b) => b.Entity<AlphaRow>().ToTable("legacy_rows"); }
public sealed class SplitHierarchyContext() : GuardContext(Options()) { protected override void Add(ModelBuilder b) { b.Entity<AlphaShape>(); b.Entity<BetaCircle>(); } }
public sealed class TwinTablesContext() : GuardContext(Options())
{
    protected override void Add(ModelBuilder b)
    {
        b.Entity<AlphaTwin>().ToTable("TBL_TWIN", "core");
        b.Entity<BetaTwin>().ToTable("TBL_TWIN", "reporting");
    }
}
public sealed class CrossNavigationContext() : GuardContext(Options())
{
    protected override void Add(ModelBuilder b)
    {
        b.Entity<AlphaRow>();
        b.Entity<BetaOrder>();
        b.Entity<BetaInvoice>().HasOne<AlphaRow>().WithMany().HasForeignKey(i => i.AlphaRowId).OnDelete(DeleteBehavior.Restrict);
    }
}
public sealed class TwoModulesContext() : GuardContext(Options())
{
    protected override void Add(ModelBuilder b)
    {
        b.Entity<AlphaRow>();
        b.Entity<BetaInvoice>().HasOne<AlphaRow>().WithMany().HasForeignKey(i => i.AlphaRowId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class ModuleGuardTests
{
    private static IReadOnlyList<string> ProblemsOf(DbContext context)
    {
        using (context) return DataModelGuard.FindProblems(context.Model);
    }

    [Test]
    public void T15_AnEntityWithNoModule_FailsStartup_NamingIt()
    {
        using var context = new ModulelessContext();
        DataModelGuard.FindProblems(context.Model).Should().ContainSingle()
            .Which.Should().StartWith("GuardModuleless:").And.Contain("declares no module");
        FluentActions.Invoking(() => DataModelGuard.AssertModel(context.Model)).Should().Throw<InvalidOperationException>()
            .WithMessage("*GuardModuleless*\"Modules\"*");
    }

    // Pass 60c: the two schema rows (two modules sharing a schema; an invalid or reserved schema) are gone with
    // ModuleDefinition.Schema. A module no longer chooses a schema; every business table is in core.

    [Test]
    public void P60c_AModuleEntityHandNamedIntoPublic_FailsStartup()
    {
        // Decision Q-B. ToTable(name) with no schema leaves the table where it was put (owner decision D1), which is the
        // default schema; public holds Identity and the template's own tables only (D2), so the guard refuses it.
        ProblemsOf(new HandNamedIntoPublicContext()).Should().ContainSingle()
            .Which.Should().StartWith("AlphaRow:").And.Contain("'legacy_rows'").And.Contain("default schema")
            .And.Contain("core.\"TBL_ALPHA_ROWS\"");
    }

    [Test]
    public void T15_ATphLeafInAnotherModule_FailsStartup()
    {
        ProblemsOf(new SplitHierarchyContext()).Should().ContainSingle()
            .Which.Should().StartWith("BetaCircle:").And.Contain("hierarchy root AlphaShape");
    }

    [Test]
    public void T15_TwoTablesWithOneNameInDifferentSchemas_FailStartup()
    {
        ProblemsOf(new TwinTablesContext()).Should().ContainSingle()
            .Which.Should().Contain("'TBL_TWIN'").And.Contain("core").And.Contain("reporting");
    }

    [Test]
    public void T16_EveryModulesTablesAreInCore_AndTemplateTablesStayInTheDefault()
    {
        // Pass 60c (owner decision D1): every module's tables are in core, plural (D3).
        using var context = new TwoModulesContext();
        DataModelGuard.FindProblems(context.Model).Should().BeEmpty();

        var alpha = context.Model.FindEntityType(typeof(AlphaRow))!;
        var beta = context.Model.FindEntityType(typeof(BetaInvoice))!;
        (alpha.GetSchema(), alpha.GetTableName()).Should().Be(("core", "TBL_ALPHA_ROWS"));
        (beta.GetSchema(), beta.GetTableName()).Should().Be(("core", "TBL_BETA_INVOICES"));
        context.Model.FindEntityType(typeof(Branch))!.GetSchema().Should().BeNull();

        // The cross-module foreign key exists without a navigation.
        var foreignKey = beta.GetForeignKeys().Single(fk => fk.PrincipalEntityType == alpha);
        foreignKey.DependentToPrincipal.Should().BeNull();
        foreignKey.DeleteBehavior.Should().Be(DeleteBehavior.Restrict);
    }

    [Test]
    public void T20_ANavigationAcrossModules_FailsStartup_AndOneToATemplateEntityDoesNot()
    {
        var problems = ProblemsOf(new CrossNavigationContext());
        problems.Should().ContainSingle()
            .Which.Should().StartWith("BetaOrder.AlphaRow:").And.Contain("module GuardAlpha").And.Contain("from module GuardBeta");
        problems.Should().NotContain(p => p.Contains("BetaOrder.Branch"), "a navigation to a template entity is allowed");
    }

    /// <summary>
    /// Pass 60b (item 1): what matters about the REAL model, in the template and in every generated project: the guard
    /// finds no problem, and every entity is either the template's or declares a module. Until 1.7.1 this asserted that
    /// the model held template entities ONLY, which failed in a generated project the moment its first module entity
    /// existed.
    /// </summary>
    [Test]
    public void T20_TheRealModel_HasNoModuleProblems_AndEveryEntityIsTemplateOrModule()
    {
        using var context = new ApplicationDbContext(GuardContext.Options());
        DataModelGuard.FindProblems(context.Model).Should().BeEmpty();

        var entities = context.Model.GetEntityTypes().Where(e => !e.IsOwned() && !e.IsPropertyBag).ToList();
        entities.Should().OnlyContain(e => TemplateEntities.Contains(e.ClrType) || ModuleDefinition.Of(e.ClrType) != null,
            "an application entity declares its module");

        // And the template list cannot rot: every listed type is in the model. The passkey table enters the model only
        // through Identity's options on the application's service provider (ModelMatchesMigrationsTests), not this bare context.
        TemplateEntities.Types.Where(t => t != typeof(Microsoft.AspNetCore.Identity.IdentityUserPasskey<string>))
            .Should().OnlyContain(t => entities.Any(e => e.ClrType == t), "TemplateEntities lists only real tables");
    }

    [Test]
    public void T20_TheSameCheck_NamesAnEntityThatIsNeitherTemplateNorModule()
    {
        using var context = new ModulelessContext();
        context.Model.GetEntityTypes().Where(e => !e.IsOwned() && !e.IsPropertyBag)
            .Where(e => !TemplateEntities.Contains(e.ClrType) && ModuleDefinition.Of(e.ClrType) == null)
            .Select(e => e.ClrType).Should().Equal(typeof(GuardModuleless));
    }
}
