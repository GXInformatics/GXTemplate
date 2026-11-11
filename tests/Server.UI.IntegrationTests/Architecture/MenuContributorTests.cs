#nullable enable
using System.Linq;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Server.UI.Models.NavigationMenu;
using CleanArchitecture.Blazor.Server.UI.Services.Navigation;
using FluentAssertions;
using Moq;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests.Architecture;

/// <summary>Pass 60 plan §5 row 27: a module adds its menu section without editing MenuService.</summary>
[TestFixture]
public class MenuContributorTests
{
    private sealed class Contributor(int order, string title) : IMenuContributor
    {
        public int Order { get; } = order;
        public MenuSectionModel Section { get; } = new() { Title = title };
    }

    private static IIdleTimeoutSettings Idle() => Mock.Of<IIdleTimeoutSettings>(s => s.Enabled == true);

    [Test]
    public void T27_ContributedSections_AppearInOrder_BetweenApplicationAndManagement()
    {
        var menu = new MenuService(Idle(), [new Contributor(20, "Sales"), new Contributor(10, "Inventory")]);

        menu.Features.Select(s => s.Title).Should().Equal("Application", "Inventory", "Sales", "MANAGEMENT");
    }

    [Test]
    public void T27_WithNoModules_TheMenuIsTheTemplatesOwn()
    {
        new MenuService(Idle()).Features.Select(s => s.Title).Should().Equal("Application", "MANAGEMENT");
        new MenuService(Idle(), []).Features.Select(s => s.Title).Should().Equal("Application", "MANAGEMENT");
    }
}
