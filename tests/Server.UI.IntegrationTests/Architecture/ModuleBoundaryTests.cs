#nullable enable
using System;
using System.Linq;
using System.Reflection;
using CleanArchitecture.Blazor.ArchitectureSamples.Modules.SampleBeta.Components;
using CleanArchitecture.Blazor.ArchitectureSamples.Modules.SampleBeta.Configurations;
using CleanArchitecture.Blazor.ArchitectureSamples.Modules.SampleBeta.Right;
using CleanArchitecture.Blazor.ArchitectureSamples.Modules.SampleBeta.Wrong;
using CleanArchitecture.Blazor.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests.Architecture;

/// <summary>
/// Architecture rule 1 (Pass 60 plan §5 rows 19 and 36 to 41): a module references another only through its
/// published Contracts. Every generated project runs <see cref="TheApplication_KeepsItsModulesApart"/> on its own
/// modules; the wrong samples prove the rule can see each way a boundary is crossed (amendment C2).
/// </summary>
[TestFixture]
public class ModuleBoundaryTests
{
    /// <summary>The application's four layers, as a generated project builds them.</summary>
    private static Assembly[] ApplicationAssemblies() =>
    [
        typeof(CleanArchitecture.Blazor.Domain.Common.Modules.ModuleDefinition).Assembly,
        typeof(CleanArchitecture.Blazor.Application.DependencyInjection).Assembly,
        typeof(CleanArchitecture.Blazor.Infrastructure.DependencyInjection).Assembly,
        typeof(CleanArchitecture.Blazor.Server.UI.DependencyInjection).Assembly
    ];

    // ---- 19: the application -------------------------------------------------------------------------

    [Test]
    public void T19_TheApplication_KeepsItsModulesApart()
    {
        var result = ModuleBoundaryRule.Check(ApplicationAssemblies());

        result.Unresolved.Should().BeEmpty("a token the scanner could not read would be a reference it cannot see");
        result.Violations.Should().BeEmpty(
            "a module may use another only through that module's Contracts namespace (README, \"Modules and schemas\"):\n" +
            string.Join("\n", result.Violations));
    }

    [Test]
    public void T19_TheScanner_ReallyReadsTheApplication()
    {
        // Guards the test above from passing vacuously: the template has no modules, so "no violations" is
        // true of a scanner that reads nothing. It must at least see the template's own well-known references.
        var references = TypeReferenceScanner.Scan(typeof(CleanArchitecture.Blazor.Application.Pipeline.TransactionBehaviour<,>)).References;
        references.Should().Contain(r => r.To == typeof(CleanArchitecture.Blazor.Application.Common.ExceptionHandlers.PostingRejectedException));
        TypeReferenceScanner.Scan(typeof(CleanArchitecture.Blazor.Server.UI.DependencyInjection).Assembly).References.Should().HaveCountGreaterThan(1000);
    }

    [Test]
    public void T19_UsingAnotherModulesContracts_AndAConfigurationsForeignKey_Pass()
    {
        Clean(typeof(UsesAlphaContracts));
        Clean(typeof(SampleBetaItem));
        Clean(typeof(SampleBetaItemConfiguration));
    }

    [Test]
    public void T19_TheSamplesAssembly_FailsOnEveryWrongCase_AndNoOther()
    {
        var result = ModuleBoundaryRule.Check(typeof(CaseA_GenericMethodArgument).Assembly);
        result.Unresolved.Should().BeEmpty();

        var offenders = result.Violations.Select(v => v.Reference.From).Distinct().ToList();
        offenders.Should().BeEquivalentTo(new[]
        {
            typeof(CaseA_GenericMethodArgument), typeof(CaseB_ExpressionLambda), typeof(CaseB_DelegateLambda),
            typeof(CaseC_AsyncMethod), typeof(CaseD_Iterator), typeof(CaseD_LocalFunction),
            typeof(CaseE_ParameterOfAlphaEntity), typeof(CaseE_RendersAlphaComponent),
            typeof(CaseF_FieldGenericArgument), typeof(CaseF_BaseTypeGenericArgument), typeof(CaseF_AttributeTypeArgument),
            // Pass 60b, amendment E2: a module using application-level code; a module sending another module's command;
            // application-level code using a module's entity.
            typeof(CaseG_ReferencesAppLevel), typeof(CaseH_SendsAnotherModulesCommand),
            typeof(CleanArchitecture.Blazor.ArchitectureSamples.AppLevel.AppLevelUsesAModuleEntity)
        });
        result.Violations.Should().OnlyContain(v =>
            (v.FromModule == "SampleBeta" && v.ToModule == "SampleAlpha")
            || (v.FromModule == "SampleBeta" && v.ToModule == ModuleBoundaryRule.AppLevel)
            || (v.FromModule == ModuleBoundaryRule.AppLevel && v.ToModule == "SampleAlpha"));
    }

    // ---- 36 to 41: amendment C2, one case at a time ----------------------------------------------------

    [Test]
    public void T36_C2a_AGenericMethodArgument() =>
        Fails(typeof(CaseA_GenericMethodArgument), "SampleAlphaItem", "<");

    [Test]
    public void T37_C2b_AnExpressionTreeLambda() =>
        Fails(typeof(CaseB_ExpressionLambda), "SampleAlphaItem", "ldtoken");

    [Test]
    public void T37_C2b_ADelegateLambda() =>
        Fails(typeof(CaseB_DelegateLambda), "SampleAlphaItem", "<>c");

    [Test]
    public void T38_C2c_AnAsyncMethod_IsReportedAgainstItsDeclaringType()
    {
        var violation = Fails(typeof(CaseC_AsyncMethod), "SampleAlphaItem", "d__");
        violation.Reference.From.Should().Be(typeof(CaseC_AsyncMethod), "not the compiler's state machine");
    }

    [Test]
    public void T39_C2d_AnIterator() =>
        Fails(typeof(CaseD_Iterator), "SampleAlphaItem", "d__");

    [Test]
    public void T39_C2d_ALocalFunction() =>
        Fails(typeof(CaseD_LocalFunction), "SampleAlphaItem", "g__Read");

    [Test]
    public void T40_C2e_ARazorComponentWithAnotherModulesEntityAsAParameter() =>
        Fails(typeof(CaseE_ParameterOfAlphaEntity), "SampleAlphaItem", "property Item");

    [Test]
    public void T40_C2e_ARazorComponentRenderingAnotherModulesComponent() =>
        Fails(typeof(CaseE_RendersAlphaComponent), "AlphaBadge", "OpenComponent");

    [Test]
    public void T41_C2f_AFieldsGenericArgument() =>
        Fails(typeof(CaseF_FieldGenericArgument), "SampleAlphaItem", "field _items");

    [Test]
    public void T41_C2f_ABaseTypesGenericArgument() =>
        Fails(typeof(CaseF_BaseTypeGenericArgument), "SampleAlphaItem", "base type");

    [Test]
    public void T41_C2f_AnAttributesTypeArgument() =>
        Fails(typeof(CaseF_AttributeTypeArgument), "SampleAlphaItem", "typeof argument");

    // ---- helpers -------------------------------------------------------------------------------------

    private static ModuleViolation Fails(Type sample, string target, string whereContains)
    {
        var result = ModuleBoundaryRule.Check(sample);
        result.Unresolved.Should().BeEmpty();
        result.Violations.Should().NotBeEmpty($"{sample.Name} crosses into SampleAlpha, and rule 1 must see it");
        result.Violations.Should().OnlyContain(v => v.Reference.From == sample);
        Func<ModuleViolation, bool> matches = v => v.Reference.To.Name == target && v.Reference.Where.Contains(whereContains);
        result.Violations.Where(matches).Should().NotBeEmpty(
            $"the reference is found where the compiler put it; found:\n{string.Join("\n", result.Violations)}");
        return result.Violations.First(matches);
    }

    private static void Clean(Type sample)
    {
        var result = ModuleBoundaryRule.Check(sample);
        result.Unresolved.Should().BeEmpty();
        result.Violations.Should().BeEmpty(string.Join("\n", result.Violations));
    }
}
