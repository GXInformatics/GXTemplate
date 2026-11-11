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
    public override string Schema => "talpha";
}

public sealed class GuardBetaModule : ModuleDefinition
{
    public override string Name => "GuardBeta";
    public override string Schema => "tbeta";
}

public sealed class SameSchemaModule : ModuleDefinition
{
    public override string Name => "SameSchema";
    public override string Schema => "talpha";
}

public sealed class BadSchemaModule : ModuleDefinition
{
    public override string Name => "BadSchema";
    public override string Schema => "Inv-1";
}

public sealed class PublicSchemaModule : ModuleDefinition
{
    public override string Name => "PublicSchema";
    public override string Schema => "public";
}

public class Moduleless : BaseEntity { public string? Name { get; set; } }

[Module<GuardAlphaModule>] public class AlphaRow : BaseEntity { public string? Name { get; set; } }
[Module<GuardBetaModule>] public class BetaRow : BaseEntity { public string? Name { get; set; } }
[Module<SameSchemaModule>] public class SameSchemaRow : BaseEntity { public string? Name { get; set; } }
[Module<BadSchemaModule>] public class BadSchemaRow : BaseEntity { public string? Name { get; set; } }
[Module<PublicSchemaModule>] public class PublicSchemaRow : BaseEntity { public string? Name { get; set; } }

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

public sealed class ModulelessContext() : GuardContext(Options()) { protected override void Add(ModelBuilder b) => b.Entity<Moduleless>(); }
public sealed class SharedSchemaContext() : GuardContext(Options()) { protected override void Add(ModelBuilder b) { b.Entity<AlphaRow>(); b.Entity<SameSchemaRow>(); } }
public sealed class BadSchemaContext() : GuardContext(Options()) { protected override void Add(ModelBuilder b) { b.Entity<BadSchemaRow>(); b.Entity<PublicSchemaRow>(); } }
public sealed class SplitHierarchyContext() : GuardContext(Options()) { protected override void Add(ModelBuilder b) { b.Entity<AlphaShape>(); b.Entity<BetaCircle>(); } }
public sealed class TwinTablesContext() : GuardContext(Options())
{
    protected override void Add(ModelBuilder b)
    {
        b.Entity<AlphaTwin>().ToTable("TBL_TWIN");
        b.Entity<BetaTwin>().ToTable("TBL_TWIN");
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
            .Which.Should().StartWith("Moduleless:").And.Contain("declares no module");
        FluentActions.Invoking(() => DataModelGuard.AssertModel(context.Model)).Should().Throw<InvalidOperationException>()
            .WithMessage("*Moduleless*Modules and schemas*");
    }

    [Test]
    public void T15_TwoModulesWithOneSchema_FailStartup()
    {
        ProblemsOf(new SharedSchemaContext()).Should().ContainSingle()
            .Which.Should().Contain("GuardAlphaModule").And.Contain("SameSchemaModule").And.Contain("'talpha'");
    }

    [Test]
    public void T15_AnInvalidOrReservedSchema_FailsStartup()
    {
        var problems = ProblemsOf(new BadSchemaContext());
        problems.Should().Contain(p => p.Contains("BadSchemaModule") && p.Contains("lower-case"));
        problems.Should().Contain(p => p.Contains("PublicSchemaModule") && p.Contains("reserved"));
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
            .Which.Should().Contain("'TBL_TWIN'").And.Contain("talpha").And.Contain("tbeta");
    }

    [Test]
    public void T16_EachModulesTablesLandInItsSchema_AndTemplateTablesStayInTheDefault()
    {
        using var context = new TwoModulesContext();
        DataModelGuard.FindProblems(context.Model).Should().BeEmpty();

        var alpha = context.Model.FindEntityType(typeof(AlphaRow))!;
        var beta = context.Model.FindEntityType(typeof(BetaInvoice))!;
        (alpha.GetSchema(), alpha.GetTableName()).Should().Be(("talpha", "TBL_ALPHA_ROW"));
        (beta.GetSchema(), beta.GetTableName()).Should().Be(("tbeta", "TBL_BETA_INVOICE"));
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
            .Select(e => e.ClrType).Should().Equal(typeof(Moduleless));
    }
}
