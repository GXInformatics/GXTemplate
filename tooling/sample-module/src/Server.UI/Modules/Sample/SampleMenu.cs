using CleanArchitecture.Blazor.Server.UI.Models.NavigationMenu;
using CleanArchitecture.Blazor.Server.UI.Services.Navigation;

namespace CleanArchitecture.Blazor.Server.UI.Modules.Sample;

/// <summary>The sample module's menu section, found by scan (pass 60).</summary>
public sealed class SampleMenu : IMenuContributor
{
    public const string Title = "SAMPLE";

    public int Order => 10;

    public MenuSectionModel Section { get; } = new()
    {
        Title = Title,
        SectionItems = new List<MenuSectionItemModel>
        {
            new() { Title = "Bins", Icon = Icons.Material.Filled.Inventory2, Href = "/sample/bins" },
            new() { Title = "Warehouses", Icon = Icons.Material.Filled.Warehouse, Href = "/sample/warehouses" }
        }
    };
}
