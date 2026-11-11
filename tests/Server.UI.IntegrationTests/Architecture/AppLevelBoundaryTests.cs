#nullable enable
using System.Linq;
using CleanArchitecture.Blazor.ArchitectureSamples.AppLevel;
using CleanArchitecture.Blazor.ArchitectureSamples.Modules.SampleBeta.Wrong;
using CleanArchitecture.Blazor.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests.Architecture;

/// <summary>
/// Pass 60b plan §5 row 20 (amendment E2): application-level code may send module commands and use module Contracts;
/// no module may reference it; it holds no entities.
/// </summary>
[TestFixture]
public class AppLevelBoundaryTests
{
    [Test]
    public void T20_AModuleReferencingAppLevelCode_FailsTheBoundaryRule()
    {
        var result = ModuleBoundaryRule.Check(typeof(CaseG_ReferencesAppLevel));
        result.Violations.Should().NotBeEmpty();
        result.Violations.Should().OnlyContain(v => v.FromModule == "SampleBeta" && v.ToModule == ModuleBoundaryRule.AppLevel
                                                    && v.Reference.To == typeof(AppLevelHelper));
    }

    [Test]
    public void T20_AppLevelCode_SendingAModuleCommand_AndUsingItsContracts_Passes()
    {
        var result = ModuleBoundaryRule.Check(typeof(AppLevelSendsAModuleCommand));
        result.Unresolved.Should().BeEmpty();
        result.Violations.Should().BeEmpty(string.Join("\n", result.Violations));

        // Not vacuous: the scanner sees the command and the contract.
        var references = TypeReferenceScanner.Scan(typeof(AppLevelSendsAModuleCommand)).References.Select(r => r.To.Name).ToList();
        references.Should().Contain("CreateAlphaItemCommand").And.Contain("IAlphaItemLookup");
    }

    [Test]
    public void T20_AppLevelCode_UsingAModuleEntity_Fails()
    {
        ModuleBoundaryRule.Check(typeof(AppLevelUsesAModuleEntity)).Violations.Should().NotBeEmpty()
            .And.OnlyContain(v => v.FromModule == ModuleBoundaryRule.AppLevel && v.ToModule == "SampleAlpha"
                                  && v.Reference.To.Name == "SampleAlphaItem");
    }

    [Test]
    public void T20_AModuleSendingAnotherModulesCommand_StillFails()
    {
        // The request exemption is the application level's only: between modules, a command is internal (rule 9).
        ModuleBoundaryRule.Check(typeof(CaseH_SendsAnotherModulesCommand)).Violations.Should().NotBeEmpty()
            .And.OnlyContain(v => v.FromModule == "SampleBeta" && v.ToModule == "SampleAlpha");
    }

    [Test]
    public void T20_AnEntityInAnAppLevelNamespace_FailsRule8()
    {
        ModuleDeclarationRules.MisdeclaredEntities([typeof(AppLevelThing)]).Should().ContainSingle()
            .Which.Should().Contain(nameof(AppLevelThing)).And.Contain("holds no entities");
    }
}
