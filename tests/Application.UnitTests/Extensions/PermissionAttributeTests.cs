#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CleanArchitecture.Blazor.Application.Common.Security;
using FluentAssertions;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Extensions;

/// <summary>
/// Pass 60b plan §5 row 19 (decision 2, amendment E2): an application's permission constant, wherever it is declared,
/// is decided by attribute instead of an edit to the template's lists.
/// </summary>
public class PermissionAttributeTests
{
    /// <summary>Stands for an application-level permission class (it would be nested in the partial Permissions).</summary>
    private static class AppLevelPermissions
    {
        [AdministratorGrant] public const string Granted = "Permissions.SampleTools.Use";
        [AdministratorExcluded("Developers only.")] public const string Excluded = "Permissions.SampleTools.Reset";
        public const string Undecided = "Permissions.SampleTools.Undecided";
    }

    private static class Contradictions
    {
        [AdministratorGrant, AdministratorExcluded("x")] public const string Both = "Permissions.Bad.Both";
        [AdministratorExcluded(" ")] public const string BlankReason = "Permissions.Bad.Blank";
        [AdministratorGrant] public const string AlsoListed = "Permissions.Template.Listed";
        [AdministratorGrant] public const string Approve = "Permissions.Bad.Approve";
    }

    private static IEnumerable<FieldInfo> FieldsOf(Type type, params string[] names) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => names.Length == 0 || names.Contains(f.Name));

    private static readonly string[] TemplateGranted = ["Permissions.Template.Listed"];
    private static readonly string[] TemplateExcluded = ["Permissions.Template.Excluded"];

    [Test]
    public void T19_AnApplicationLevelPermissionDecidedByAttribute_PassesTheRegistryCheck_AndAGrantReachesTheAdministrator()
    {
        var decisions = AdministratorPermissionRegistry.DecideByAttribute(
            FieldsOf(typeof(AppLevelPermissions), "Granted", "Excluded"), TemplateGranted, TemplateExcluded, []);

        decisions.Problems.Should().BeEmpty();
        decisions.Granted.Should().Equal("Permissions.SampleTools.Use");
        decisions.Excluded.Should().ContainKey("Permissions.SampleTools.Reset").WhoseValue.Should().Be("Developers only.");

        var all = TemplateGranted.Concat(TemplateExcluded).Append("Permissions.SampleTools.Use").Append("Permissions.SampleTools.Reset").ToList();
        FluentActions.Invoking(() => AdministratorPermissionRegistry.Validate(all,
                TemplateGranted.Concat(decisions.Granted).ToList(), TemplateExcluded.Concat(decisions.Excluded.Keys).ToList()))
            .Should().NotThrow();
    }

    [Test]
    public void T19_TheSameConstantWithNoDecision_FailsStartup_NamingIt()
    {
        var decisions = AdministratorPermissionRegistry.DecideByAttribute(
            FieldsOf(typeof(AppLevelPermissions)), TemplateGranted, TemplateExcluded, []);
        var all = TemplateGranted.Concat(TemplateExcluded)
            .Concat(FieldsOf(typeof(AppLevelPermissions)).Select(f => (string)f.GetValue(null)!)).ToList();

        FluentActions.Invoking(() => AdministratorPermissionRegistry.Validate(all,
                TemplateGranted.Concat(decisions.Granted).ToList(), TemplateExcluded.Concat(decisions.Excluded.Keys).ToList()))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*Neither granted nor excluded*[AdministratorGrant]*Permissions.SampleTools.Undecided*");
    }

    [Test]
    public void T19_ContradictoryDecisions_EachFail_NamingTheConstant()
    {
        var problems = AdministratorPermissionRegistry.DecideByAttribute(
            FieldsOf(typeof(Contradictions)), TemplateGranted, TemplateExcluded, ["Permissions.Bad.Approve"]).Problems;

        problems.Should().HaveCount(4);
        problems.Should().Contain(p => p.Contains("Permissions.Bad.Both") && p.Contains("both"));
        problems.Should().Contain(p => p.Contains("Permissions.Bad.Blank") && p.Contains("needs a reason"));
        problems.Should().Contain(p => p.Contains("Permissions.Template.Listed") && p.Contains("also carries an attribute"));
        problems.Should().Contain(p => p.Contains("Permissions.Bad.Approve") && p.Contains("never approves"));
    }

    [Test]
    public void T19_TheTemplate_DecidesNothingByAttribute_AndItsRegistryHolds()
    {
        AdministratorPermissionRegistry.DecideByAttribute(AdministratorPermissionRegistry.PermissionFields(),
            AdministratorPermissionRegistry.Granted, AdministratorPermissionRegistry.Excluded.Keys.ToList(), []).Problems.Should().BeEmpty();
        FluentActions.Invoking(AdministratorPermissionRegistry.AssertNoDivergence).Should().NotThrow();
        AdministratorPermissionRegistry.EffectiveGranted.Should().Contain(AdministratorPermissionRegistry.Granted);
    }
}
