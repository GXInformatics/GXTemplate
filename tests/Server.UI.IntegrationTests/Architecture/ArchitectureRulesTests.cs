#nullable enable
using System;
using System.IO;
using System.Linq;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Models;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Transactions;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Server.UI.IntegrationTests.Architecture.Samples;
using CleanArchitecture.Blazor.Server.UI.IntegrationTests.Architecture.Samples.Entities;
using CleanArchitecture.Blazor.Server.UI.IntegrationTests.Architecture.Samples.Modules.SampleAlpha;
using CleanArchitecture.Blazor.TestSupport;
using FluentAssertions;
using Mediator;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests.Architecture;

/// <summary>
/// Architecture rules 4 to 8 and 10 (Pass 60 plan §5 rows 22 to 26 and 42). Each passes on the application and
/// fails on a deliberately wrong sample. Generated into every project, so they hold for its own modules too.
/// </summary>
[TestFixture]
public class ArchitectureRulesTests
{
    private static System.Collections.Generic.IEnumerable<(string File, string Source)> Sources() =>
        SourceTree.SourceFiles().Select(f => (SourceTree.Relative(f), File.ReadAllText(f)));

    // ---- 22: rule 4 ----------------------------------------------------------------------------------

    [Test]
    public void T22_TheBranchFilterIsRemovedOnlyByAcrossBranches_AndNothingDropsEveryFilter()
    {
        var findings = Sources().SelectMany(s => SourceRules.BranchFilterRemovals(s.File, s.Source)).ToList();
        findings.Should().BeEmpty("only AcrossBranchesAsync may remove the branch filter (pass 59, B5):\n" + string.Join("\n", findings));
    }

    [Test]
    public void T22_Rule4_FailsOnAPlantedRemoval_AndOnABareCall_AndAllowsTheTenantScopes()
    {
        const string planted = """
            var rows = db.Stock.IgnoreQueryFilters([QueryFilters.Branch]).ToList();
            var all = db.Stock.IgnoreQueryFilters().ToList();
            var tenant = db.Audit.IgnoreQueryFilters([QueryFilters.Tenant]).ToList();
            // db.X.IgnoreQueryFilters() in a comment is not a call
            """;
        var findings = SourceRules.BranchFilterRemovals("src/Application/Modules/Inventory/Planted.cs", planted);
        findings.Select(f => f.Line).Should().Equal(1, 2);

        SourceRules.BranchFilterRemovals(SourceRules.BranchScopeFile, "return query.IgnoreQueryFilters([QueryFilters.Branch]);")
            .Should().BeEmpty("BranchQueryScope is the sanctioned helper");
        SourceRules.BranchFilterRemovals(SourceRules.BranchScopeFile, "return query.IgnoreQueryFilters();")
            .Should().ContainSingle("not even the helper may drop every filter");
    }

    // ---- 23: rule 5 ----------------------------------------------------------------------------------

    [Test]
    public void T23_BulkWritesAndRawSql_AppearOnlyInTheListedFiles()
    {
        var findings = Sources().SelectMany(s => SourceRules.RawSqlOutsideListedFiles(s.File, s.Source)).ToList();
        findings.Should().BeEmpty("bulk writes and raw SQL bypass the save rules; add a file to the list only after review:\n" +
                                  string.Join("\n", findings));
    }

    [Test]
    public void T23_TheListedFilesExist_SoTheListCannotRot()
    {
        var root = SourceTree.RepositoryRoot();
        foreach (var file in SourceRules.RawSqlFiles)
            File.Exists(Path.Combine(root, file)).Should().BeTrue($"{file} is on rule 5's list; remove it if it is gone");
    }

    [Test]
    public void T23_Rule5_FailsOnAPlantedBulkWrite()
    {
        const string planted = """
            await db.Stock.Where(s => s.Qty < 0).ExecuteUpdateAsync(s => s.SetProperty(x => x.Qty, 0));
            await db.Database.ExecuteSqlRawAsync("DELETE FROM x");
            var n = await db.Database.SqlQuery<int>($"SELECT 1").SingleAsync();
            """;
        SourceRules.RawSqlOutsideListedFiles("src/Application/Modules/Inventory/Planted.cs", planted)
            .Select(f => f.Line).Should().Equal(1, 2, 3);
        SourceRules.RawSqlOutsideListedFiles("src/Infrastructure/Persistence/Transactions/PostingSql.cs", planted)
            .Should().BeEmpty();
    }

    // ---- 24: rule 6 ----------------------------------------------------------------------------------

    [Test]
    public void T24_EveryIdempotentRequestReturnsResult_AtStartup()
    {
        RequestContractRegistry.FindProblems(typeof(CleanArchitecture.Blazor.Application.DependencyInjection).Assembly.GetTypes())
            .Should().BeEmpty();
    }

    public sealed class IdempotentReturningInt : IRequest<int>, IIdempotentRequest
    {
        public Guid RequestId { get; init; } = Guid.NewGuid();
    }

    public sealed class IdempotentReturningResult : IRequest<Result<int>>, IIdempotentRequest
    {
        public Guid RequestId { get; init; } = Guid.NewGuid();
    }

    [Test]
    public void T24_Rule6_FailsAtStartupOnAnIdempotentRequestReturningInt()
    {
        RequestContractRegistry.FindProblems([typeof(IdempotentReturningInt), typeof(IdempotentReturningResult)])
            .Should().ContainSingle().Which.Should().Contain(nameof(IdempotentReturningInt)).And.Contain("Result or Result<T>");
        FluentActions.Invoking(() => RequestContractRegistry.AssertSound([typeof(IdempotentReturningInt)], "planted"))
            .Should().Throw<InvalidOperationException>().WithMessage("*IdempotentReturningInt*");
    }

    // ---- 25: rule 7 ----------------------------------------------------------------------------------

    [Test]
    public void T25_TheNumberRulesRunAtStartup()
    {
        // Rule 7 is pass 59's DataModelGuard.CheckNumbers (its rows 1 to 3 are the samples); nothing is
        // duplicated. What this pins is that the startup path still runs the guard.
        var initializer = File.ReadAllText(Path.Combine(SourceTree.RepositoryRoot(),
            "src", "Infrastructure", "Persistence", "ApplicationDbContextInitializer.cs"));
        initializer.Should().Contain("DataModelGuard.AssertModel(");
    }

    // ---- 26: rule 8 ----------------------------------------------------------------------------------

    [Test]
    public void T26_EveryModuleEntityDeclaresItsOwnModule()
    {
        ModuleDeclarationRules.MisdeclaredEntities(typeof(CleanArchitecture.Blazor.Domain.Common.Modules.ModuleDefinition).Assembly.GetTypes())
            .Should().BeEmpty();
    }

    [Test]
    public void T26_Rule8_FailsOnAMisdeclaredAnUndeclaredAndAMisplacedEntity()
    {
        var problems = ModuleDeclarationRules.MisdeclaredEntities(
            [typeof(MisdeclaredInAlpha), typeof(UndeclaredInAlpha), typeof(DeclaredInAlpha), typeof(ModuleEntityInTemplateFolder)]);

        problems.Should().HaveCount(3);
        problems.Should().Contain(p => p.Contains(nameof(MisdeclaredInAlpha)) && p.Contains("declares module SampleBeta"));
        problems.Should().Contain(p => p.Contains(nameof(UndeclaredInAlpha)) && p.Contains("declares no [Module<>]"));
        problems.Should().Contain(p => p.Contains(nameof(ModuleEntityInTemplateFolder)) && p.Contains("not under Modules.SampleAlpha"));
    }

    // ---- 42: rule 10 (amendment C3) ------------------------------------------------------------------

    [Test]
    public void T42_TheSharedContextDeclaresNoDbSetOfAModuleEntity()
    {
        ModuleDeclarationRules.ModuleDbSets(typeof(ApplicationDbContext)).Should().BeEmpty();
        ModuleDeclarationRules.ModuleDbSets(typeof(IApplicationDbContext)).Should().BeEmpty();
    }

    [Test]
    public void T42_Rule10_FailsOnAPlantedDbSet()
    {
        ModuleDeclarationRules.ModuleDbSets(typeof(PlantedDbSetContext))
            .Should().ContainSingle().Which.Should().Contain("AlphaItems").And.Contain("Set<SampleAlphaItem>()");
    }
}
