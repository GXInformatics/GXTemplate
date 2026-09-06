using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Server.UI.Middlewares;
using CleanArchitecture.Blazor.Server.UI.Models.NavigationMenu;

namespace CleanArchitecture.Blazor.Server.UI.Services.Navigation;

/// <summary>
/// The navigation menu's definition.
/// </summary>
/// <remarks>
/// <para>
/// <b>The menu gates by ROLE; every page gates by PERMISSION. That is deliberate, and it is the one
/// thing to know before reading an entry below.</b> <c>NavigationMenu.razor</c> filters at three
/// levels - section, item and sub-item - on <c>x.Roles == null || x.Roles.Any(r =&gt; Roles.Contains(r))</c>,
/// where <c>Roles</c> is the signed-in user's assigned role names. There is exactly ONE gate in the
/// whole menu: <c>Roles = [Admin]</c> on the MANAGEMENT section. No section item and no sub-item
/// carries a gate of its own, so all eleven entries under it - Multi-Tenant, Users, Roles, Profile,
/// Login History, Picklist, Security Settings, Audit Trails, Email Templates, Logs, Jobs - inherit
/// that one.
/// </para>
/// <para>
/// <b>An entry visible to someone who cannot use the page is therefore EXPECTED, not a gap.</b> The
/// page's own <c>[Authorize(Policy = Permissions.*)]</c> - and, for anything behind Mediator,
/// <c>AuthorizationBehaviour</c>'s deny-by-default - is the enforcement. The menu is navigation. A
/// principal holding the <c>Admin</c> role but a customised permission set will see links they are
/// refused at, and that is the designed behaviour: <c>MenuSectionSubItemModel</c> has a
/// <c>Roles</c> array and no permission field, so the menu cannot express a permission even in
/// principle.
/// </para>
/// <para>
/// <b>This is written down because it has already cost a pass.</b> Pass 34 §2.3 reported the "Logs"
/// entry as ungated and inconsistent with its neighbours; Pass 35 A4 established that the neighbours
/// carry no permission either and the gate sits one level up, so nothing needed changing - and
/// adding an item-level gate to Logs alone would have CREATED the inconsistency the finding
/// described. <c>SystemMenuGateComponentTests.NoMenuEntryCarriesAGateOfItsOwn</c> asserts the idiom
/// so a future "fix" of that shape fails rather than lands.
/// </para>
/// </remarks>
public class MenuService : IMenuService
{
    /// <summary>
    /// Builds the menu, dropping any surface that belongs to a switched-off feature.
    /// </summary>
    /// <remarks>
    /// The security-settings route answers 404 when the idle timeout is disabled
    /// (<see cref="SecuritySettingsPageMiddleware"/>), so leaving its entry in the menu would offer a
    /// link straight to a 404. Removed here, once, rather than omitted from the declaration below, so
    /// that the menu stays one readable list.
    /// </remarks>
    public MenuService(IIdleTimeoutSettings idleTimeoutSettings)
    {
        if (idleTimeoutSettings.Enabled) return;

        foreach (var section in _features)
        {
            foreach (var item in section.SectionItems ?? [])
            {
                var children = item.MenuItems;
                if (children is null) continue;

                for (var i = children.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(children[i].Href,
                            SecuritySettingsPageMiddleware.SecuritySettingsPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        children.RemoveAt(i);
                    }
                }
            }
        }
    }
    private readonly List<MenuSectionModel> _features = new()
    {
        new MenuSectionModel
        {
            Title = "Application",
            SectionItems = new List<MenuSectionItemModel>
            {
                new() { Title = "Home", Icon = Icons.Material.Filled.Home, Href = "/" },
                new()
                {
                    Title = "Content",
                    Icon = Icons.Material.Filled.ShoppingCart,
                    PageStatus = PageStatus.Completed,
                    IsParent = true,
                    MenuItems = new List<MenuSectionSubItemModel>
                    {
                        new()
                        {
                            Title = "Documents",
                            Href = "/pages/documents",
                            PageStatus = PageStatus.Completed
                        }
                    }
                }
            }
        },
        new MenuSectionModel
        {
            Title = "MANAGEMENT",
            Roles = new[] { Roles.Admin },
            SectionItems = new List<MenuSectionItemModel>
            {
                new()
                {
                    IsParent = true,
                    Title = "Authorization",
                    Icon = Icons.Material.Filled.ManageAccounts,
                    MenuItems = new List<MenuSectionSubItemModel>
                    {
                        new()
                        {
                            Title = "Multi-Tenant",
                            Href = "/system/tenants",
                            PageStatus = PageStatus.Completed
                        },
                        new()
                        {
                            Title = "Users",
                            Href = "/identity/users",
                            PageStatus = PageStatus.Completed
                        },
                        new()
                        {
                            Title = "Roles",
                            Href = "/identity/roles",
                            PageStatus = PageStatus.Completed
                        },
                        new()
                        {
                            Title = "Profile",
                            Href = "/user/profile",
                            PageStatus = PageStatus.Completed
                        },
                        new()
                        {
                            Title = "Login History",
                            Href = "/pages/identity/loginaudits",
                            PageStatus = PageStatus.Completed
                        },
                    }
                },
                new()
                {
                    IsParent = true,
                    Title = "System",
                    Icon = Icons.Material.Filled.Devices,
                    MenuItems = new List<MenuSectionSubItemModel>
                    {
                        new()
                        {
                            Title = "Picklist",
                            Href = "/system/picklistset",
                            PageStatus = PageStatus.Completed
                        },
                        new()
                        {
                            Title = "Security Settings",
                            Href = "/system/security-settings",
                            PageStatus = PageStatus.Completed
                        },
                        new()
                        {
                            Title = "Audit Trails",
                            Href = "/system/audittrails",
                            PageStatus = PageStatus.Completed
                        },
                        new()
                        {
                            Title = "Email Templates",
                            Href = "/pages/system/email-templates",
                            PageStatus = PageStatus.Completed
                        },
                        new()
                        {
                            Title = "Logs",
                            Href = "/system/logs",
                            PageStatus = PageStatus.Completed
                        },
                        new()
                        {
                            Title = "Jobs",
                            Href = "/jobs",
                            PageStatus = PageStatus.Completed,
                            Target = "_blank"
                        }
                    }
                }
            }
        }
    };

    public IEnumerable<MenuSectionModel> Features => _features;
}
