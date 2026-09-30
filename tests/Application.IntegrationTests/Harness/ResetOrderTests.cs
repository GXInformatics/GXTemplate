#nullable enable
using System;
using FluentAssertions;
using CleanArchitecture.Blazor.TestSupport;
using NUnit.Framework;

// Pure: no database, so outside the [SetUpFixture]'s namespace.
namespace CleanArchitecture.Blazor.IntegrationHarness.Tests;

[TestFixture]
public class ResetOrderTests
{
    [Test]
    public void EveryTableComesBeforeTheTablesItReferences()
    {
        // child -> middle -> parent, listed parent-first on purpose.
        var order = new System.Collections.Generic.List<string>(ResetOrder.ChildrenFirst(
            ["parent", "middle", "child", "unrelated"],
            [("middle", "parent"), ("child", "middle")]));

        order.Should().BeEquivalentTo(["parent", "middle", "child", "unrelated"]);
        order.IndexOf("child").Should().BeLessThan(order.IndexOf("middle"));
        order.IndexOf("middle").Should().BeLessThan(order.IndexOf("parent"));
    }

    [Test]
    public void ASelfReference_DoesNotBlockTheOrder()
    {
        // AspNetUsers.SuperiorId references AspNetUsers.
        var order = ResetOrder.ChildrenFirst(["users", "tenants"], [("users", "users"), ("users", "tenants")]);

        order.Should().Equal("users", "tenants");
    }

    [Test]
    public void AMultiTableCycle_FailsNamingTheTables()
    {
        var act = () => ResetOrder.ChildrenFirst(
            ["a", "b", "c", "free"],
            [("a", "b"), ("b", "c"), ("c", "a")]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*foreign-key cycle*a, b, c*");
    }
}
#nullable restore
