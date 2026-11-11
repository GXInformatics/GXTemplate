#nullable enable
using System;
using System.Linq;
using CleanArchitecture.Blazor.Application.Common.Security;
using FluentAssertions;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Extensions;

/// <summary>Pass 60b plan §5 row 7: the role catalogue's checks (item 3, decision 7).</summary>
public class RoleCatalogueResolveTests
{
    private static readonly string[] Known = ["Permissions.A.View", "Permissions.A.Edit", "Permissions.B.View"];

    private static ApplicationRoleDefinition Def(string name, params string[] permissions) => new(name, "A role.", permissions, "Inventory");

    [Test]
    public void T07_DeclaredRolesAndGrants_Merge_AndBasicMayReceiveGrants()
    {
        var roles = RoleCatalogue.Resolve(
            [Def("Storekeeper", "Permissions.A.View")],
            [new ApplicationRoleGrant("storekeeper", ["Permissions.A.Edit", "Permissions.A.View"], "Sales"),
             new ApplicationRoleGrant("Basic", ["Permissions.B.View"], "AppLevel")],
            Known);

        roles.Should().HaveCount(2);
        roles.Single(r => r.Name == "Storekeeper").Permissions.Should().BeEquivalentTo("Permissions.A.View", "Permissions.A.Edit");
        var basic = roles.Single(r => r.Name == "Basic");
        basic.Description.Should().BeNull("the template's role keeps its own description");
        basic.Permissions.Should().Equal("Permissions.B.View");
    }

    [Test]
    public void T07_AnEmptyCatalogue_IsNothing() =>
        RoleCatalogue.Resolve([], [], Known).Should().BeEmpty();

    [Test]
    public void T07_ARoleDeclaredTwice_Fails_NamingBothModules() =>
        FluentActions.Invoking(() => RoleCatalogue.Resolve([Def("Storekeeper"), Def("STOREKEEPER") with { DeclaredBy = "Sales" }], [], Known))
            .Should().Throw<InvalidOperationException>().WithMessage("*'Storekeeper' is declared 2 times (by Inventory, Sales)*");

    [Test]
    public void T07_AGrantToAnUndeclaredRole_Fails() =>
        FluentActions.Invoking(() => RoleCatalogue.Resolve([], [new ApplicationRoleGrant("Branch Manager", ["Permissions.A.View"], "Sales")], Known))
            .Should().Throw<InvalidOperationException>().WithMessage("*Sales grants to role 'Branch Manager', which no module declares*");

    [Test]
    public void T07_AnyEntryNamingTheAdministratorRole_Fails_WhateverThePermission()
    {
        FluentActions.Invoking(() => RoleCatalogue.Resolve([Def("Admin", "Permissions.A.View")], [], Known))
            .Should().Throw<InvalidOperationException>().WithMessage("*names the administrator role 'Admin'*decision 7*");
        FluentActions.Invoking(() => RoleCatalogue.Resolve([], [new ApplicationRoleGrant("admin", ["Permissions.A.View"], "Sales")], Known))
            .Should().Throw<InvalidOperationException>().WithMessage("*names the administrator role 'admin'*");
    }

    [Test]
    public void T07_AnUnknownPermission_Fails_NamingIt() =>
        FluentActions.Invoking(() => RoleCatalogue.Resolve([Def("Storekeeper", "Permissions.A.Veiw")], [], Known))
            .Should().Throw<InvalidOperationException>().WithMessage("*'Permissions.A.Veiw', which is not a Permissions constant*");

    [Test]
    public void T07_DeclaringBasic_ABlankName_ALongName_AndNoDescription_Fail()
    {
        var problems = FluentActions.Invoking(() => RoleCatalogue.Resolve(
                [Def("Basic"), Def(" "), Def(new string('x', 257)), new ApplicationRoleDefinition("Picker", "", [], "Inventory")], [], Known))
            .Should().Throw<InvalidOperationException>().Which.Message;

        problems.Should().Contain("declares role 'Basic', which the template declares")
            .And.Contain("names a role with no name")
            .And.Contain("longer than 256 characters")
            .And.Contain("declares role 'Picker' with no description");
    }
}
