using CleanArchitecture.Blazor.Server.UI.Models.NavigationMenu;

namespace CleanArchitecture.Blazor.Server.UI.Services.Navigation;

/// <summary>
/// A module's menu section (Pass 60), so that a module never edits the shared <see cref="MenuService"/>.
/// </summary>
/// <remarks>
/// A module implements one in <c>Server.UI/Modules/&lt;Module&gt;/&lt;Module&gt;Menu.cs</c>; every implementation in the
/// Server.UI assembly is registered at startup. <see cref="MenuService"/> places the sections, by <see cref="Order"/>,
/// between the template's "Application" and "MANAGEMENT" sections. As with the template's own entries, the menu is
/// navigation only: each page's <c>[Authorize(Policy = …)]</c> is the enforcement.
/// </remarks>
public interface IMenuContributor
{
    /// <summary>Position among the modules' sections, lowest first.</summary>
    int Order { get; }

    MenuSectionModel Section { get; }
}
