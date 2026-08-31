using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Server.UI.Middlewares;
using CleanArchitecture.Blazor.Server.UI.Models.NavigationMenu;

namespace CleanArchitecture.Blazor.Server.UI.Services.Navigation;

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
