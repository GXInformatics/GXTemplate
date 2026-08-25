#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using CleanArchitecture.Blazor.Application.Common.Security;
using FluentAssertions;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Security;

/// <summary>
/// The administrator grant is two explicit lists rather than a reflection sweep, so that adding a
/// permission constant forces a decision instead of silently granting it. These tests pin that the
/// current tree is consistent, and that each way of diverging actually fails - a guard that cannot
/// fail is not a guard.
/// </summary>
[TestFixture]
public class AdministratorPermissionRegistryTests
{
    [Test]
    public void TheCurrentTreeHasNoDivergence()
    {
        var act = () => AdministratorPermissionRegistry.AssertNoDivergence();

        act.Should().NotThrow(
            "every declared permission is either granted to the administrator or excluded with a reason");
    }

    [Test]
    public void DiscoveryFindsTheRealConstants()
    {
        var all = AdministratorPermissionRegistry.DiscoverAllPermissions();

        all.Should().NotBeEmpty();
        all.Should().Contain(Permissions.Documents.View);
        all.Should().Contain(Permissions.Users.ManageRoles);
        all.Should().OnlyHaveUniqueItems();
        all.Should().OnlyContain(p => p.StartsWith("Permissions.", StringComparison.Ordinal));
    }

    [Test]
    public void TheTwoListsCoverEveryConstantBetweenThem()
    {
        var all = AdministratorPermissionRegistry.DiscoverAllPermissions();
        var covered = AdministratorPermissionRegistry.Granted
            .Concat(AdministratorPermissionRegistry.Excluded.Keys)
            .ToList();

        covered.Should().BeEquivalentTo(all,
            "the lists and the constants are the same set, partitioned");
    }

    [Test]
    public void TheListsDoNotOverlap()
    {
        AdministratorPermissionRegistry.Granted.Should().NotIntersectWith(
            AdministratorPermissionRegistry.Excluded.Keys);
    }

    [Test]
    public void EveryExclusionCarriesARationale()
    {
        AdministratorPermissionRegistry.Excluded.Should().NotBeEmpty();
        AdministratorPermissionRegistry.Excluded.Values.Should().OnlyContain(
            reason => reason.Length > 40,
            "an exclusion without a real reason is a decision nobody can review");
    }

    // ---- the guard actually fails --------------------------------------------------------------

    [Test]
    public void AConstantInNeitherList_FailsAndNamesIt()
    {
        var all = AdministratorPermissionRegistry.DiscoverAllPermissions()
            .Append("Permissions.Invented.Feature")
            .ToList();

        var act = () => AdministratorPermissionRegistry.Validate(
            all,
            AdministratorPermissionRegistry.Granted,
            AdministratorPermissionRegistry.Excluded.Keys.ToList());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Permissions.Invented.Feature*")
            .WithMessage("*Neither granted nor excluded*");
    }

    [Test]
    public void AConstantInBothLists_FailsAndNamesIt()
    {
        var all = AdministratorPermissionRegistry.DiscoverAllPermissions();

        var act = () => AdministratorPermissionRegistry.Validate(
            all,
            AdministratorPermissionRegistry.Granted,
            // Excluded now also claims a permission that is genuinely granted.
            AdministratorPermissionRegistry.Excluded.Keys.Append(Permissions.Documents.View).ToList());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*BOTH granted and excluded*")
            .WithMessage($"*{Permissions.Documents.View}*");
    }

    [Test]
    public void AListedNameThatIsNoLongerAConstant_FailsAndNamesIt()
    {
        // The case the old reflection loop could never detect: a permission was deleted and the
        // list was left pointing at it.
        var all = AdministratorPermissionRegistry.DiscoverAllPermissions()
            .Where(p => p != Permissions.Tenants.Delete)
            .ToList();

        var act = () => AdministratorPermissionRegistry.Validate(
            all,
            AdministratorPermissionRegistry.Granted,
            AdministratorPermissionRegistry.Excluded.Keys.ToList());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*no longer a declared constant*")
            .WithMessage($"*{Permissions.Tenants.Delete}*");
    }

    [Test]
    public void ADuplicateInTheGrantedList_FailsAndNamesIt()
    {
        var all = AdministratorPermissionRegistry.DiscoverAllPermissions();

        var act = () => AdministratorPermissionRegistry.Validate(
            all,
            AdministratorPermissionRegistry.Granted.Append(Permissions.Logs.View).ToList(),
            AdministratorPermissionRegistry.Excluded.Keys.ToList());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*more than once*")
            .WithMessage($"*{Permissions.Logs.View}*");
    }

    [Test]
    public void DiscoveringNothing_Fails()
    {
        // A registry that finds no constants would otherwise pass forever while checking nothing.
        var act = () => AdministratorPermissionRegistry.Validate(
            Array.Empty<string>(),
            AdministratorPermissionRegistry.Granted,
            AdministratorPermissionRegistry.Excluded.Keys.ToList());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*no permission constants*");
    }

    [Test]
    public void TheExcludedEmailTemplatePermissions_AreTheOnesWhosePageDoesNotExist()
    {
        AdministratorPermissionRegistry.Excluded.Keys.Should().BeEquivalentTo(new[]
        {
            Permissions.EmailTemplates.View,
            Permissions.EmailTemplates.Create,
            Permissions.EmailTemplates.Edit,
            Permissions.EmailTemplates.Delete
        }, "only the email-template page is missing from this template");
    }
}
#nullable restore
