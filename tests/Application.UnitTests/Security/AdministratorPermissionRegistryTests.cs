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
    public void TheExcludedPermissions_AreTheOnesWhoseSurfaceDoesNotExist()
    {
        // Pass 26 added six to this list. It was the four email-template rights alone, and the
        // assertion said so; the list is now every constant that names a surface this template does
        // not have. Naming them exhaustively is the point of the test - an exclusion is a decision,
        // and a decision that can be added without anyone noticing is not one.
        AdministratorPermissionRegistry.Excluded.Keys.Should().BeEquivalentTo(new[]
        {
            // No email-template management page: no route, no component, no request type.
            Permissions.EmailTemplates.View,
            Permissions.EmailTemplates.Create,
            Permissions.EmailTemplates.Edit,
            Permissions.EmailTemplates.Delete,

            // No users-in-role or claims-in-role administration, and no read-only permission
            // viewer - viewing happens inside the dialog Roles.ManagePermissions already gates.
            Permissions.Roles.ManageUsersInRole,
            Permissions.Roles.ViewUsersInRole,
            Permissions.Roles.ManageClaimsInRole,
            Permissions.Roles.ViewClaimsInRole,
            Permissions.Roles.ViewPermissions,

            // The dashboard is routed at "/", so gating it would 403 every user at sign-in.
            Permissions.Dashboards.View
        }, "an exclusion names a surface this template does not have");
    }

    [Test]
    public void EveryExclusionCarriesAReason()
    {
        // The reason is what makes an exclusion reviewable: without it the list is indistinguishable
        // from a set of permissions somebody forgot to grant.
        foreach (var (permission, reason) in AdministratorPermissionRegistry.Excluded)
        {
            reason.Should().NotBeNullOrWhiteSpace($"{permission} is excluded and must say why");
            reason.Length.Should().BeGreaterThan(40, $"{permission}'s reason should explain, not label");
        }
    }
}
#nullable restore
