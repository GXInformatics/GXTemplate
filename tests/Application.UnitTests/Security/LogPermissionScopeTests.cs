#nullable enable
using System;
using System.ComponentModel;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using System.Linq;
using System.Reflection;
using CleanArchitecture.Blazor.Application.Common.Security;
using FluentAssertions;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Security;

/// <summary>
/// The three <c>Permissions.Logs.*</c> descriptions say that the right is installation-wide.
/// </summary>
/// <remarks>
/// <para>
/// <b>These strings are a security control, not decoration.</b> <c>PermissionQueryService</c> reads
/// each constant's <see cref="DescriptionAttribute"/> by reflection into
/// <c>PermissionModel.HelpText</c>, and <c>PermissionsDrawer</c> renders it under the permission's
/// name - so this text is what an administrator sees at the moment they decide whether to grant the
/// right. Pass 34 established that a `Logs.View` holder reads every tenant's usernames, client
/// addresses, mail recipient addresses, document storage keys and full exception stack traces; the
/// wording before Pass 35 was "Allows viewing log details", which said nothing about any of that.
/// </para>
/// <para>
/// <b>Why this is asserted rather than left to review.</b> Every other permission in this template
/// describes a tenant-scoped capability, so a reader reasonably assumes the product's default. These
/// three are the exception, and an exception that depends on nobody rewording it is not a control.
/// The assertions below are deliberately about MEANING - the scope must be stated - rather than
/// about an exact sentence, so the wording can be improved without a test failing for no reason.
/// </para>
/// <para>
/// <b>The rendering half is <c>LogPermissionDescriptionComponentTests</c></b>, which drives the real
/// <c>PermissionsDrawer</c>. This half proves the text exists and says the right thing; that half
/// proves an administrator actually sees it.
/// </para>
/// </remarks>
[TestFixture]
public class LogPermissionScopeTests
{
    /// <summary>Reads the description exactly as <c>PermissionQueryService</c> does.</summary>
    private static string DescriptionOf(string constantName)
    {
        var field = typeof(Permissions.Logs).GetField(
            constantName, BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        field.Should().NotBeNull($"Permissions.Logs.{constantName} must exist");

        return field!.GetCustomAttributes<DescriptionAttribute>().FirstOrDefault()?.Description
               ?? string.Empty;
    }

    private static bool StatesInstallationScope(string description) =>
        description.Contains("installation", StringComparison.OrdinalIgnoreCase)
        || description.Contains("every tenant", StringComparison.OrdinalIgnoreCase);

    [TestCase(nameof(Permissions.Logs.View))]
    [TestCase(nameof(Permissions.Logs.Search))]
    [TestCase(nameof(Permissions.Logs.Purge))]
    public void EveryLogRightStatesThatItIsInstallationWide(string constantName)
    {
        var description = DescriptionOf(constantName);

        description.Should().NotBeEmpty(
            "the role editor renders this text as the permission's help text");
        StatesInstallationScope(description).Should().BeTrue(
            $"Permissions.Logs.{constantName} reaches every tenant's rows and its description is the " +
            $"only place an administrator is told so - it currently reads: \"{description}\"");
    }

    [Test]
    public void ThePurgeRightSaysThatItErases()
    {
        // The one right whose blast radius is not merely "sees more than you expected": it is an
        // unfiltered ExecuteDelete over the whole table, there is no log export, and nothing puts
        // the rows back. "Allows purging log records" did not convey that.
        var description = DescriptionOf(nameof(Permissions.Logs.Purge));

        description.Should().ContainAny("eras", "delet", "destroy");
    }

    [Test]
    public void TheModuleDescriptionAlsoStatesTheScope()
    {
        // The class-level attribute is a separate string on a separate path: it becomes
        // PermissionModel.Description and renders as the GROUP HEADER above the three checkboxes,
        // where a reader skimming groups sees it before any help text.
        var moduleDescription = typeof(Permissions.Logs)
            .GetCustomAttributes<DescriptionAttribute>().FirstOrDefault()?.Description ?? string.Empty;

        StatesInstallationScope(moduleDescription).Should().BeTrue(
            $"the Logs group header currently reads: \"{moduleDescription}\"");
    }

    [Test]
    public void NoLogRightClaimsATenantScopeItDoesNotHave()
    {
        // The inverse control. A description promising "your organisation's" logs would be worse
        // than the vague original, because it would be an affirmative false statement rather than
        // an omission - and this is exactly the wording someone might reach for while tidying.
        foreach (var name in new[]
                 {
                     nameof(Permissions.Logs.View),
                     nameof(Permissions.Logs.Search),
                     nameof(Permissions.Logs.Purge)
                 })
        {
            var description = DescriptionOf(name);
            description.Should().NotContainAny(
                "your tenant", "own tenant", "your organisation", "own organisation");
        }
    }
}
