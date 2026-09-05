#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Bunit;
using CleanArchitecture.Blazor.Application.Common.ExceptionHandlers;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Models;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Application.Features.Identity.DTOs;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Configurations;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Services.Identity;
using CleanArchitecture.Blazor.Server.UI.Components.Dialogs;
using CleanArchitecture.Blazor.Server.UI.Pages.Identity.Roles.Components;
using CleanArchitecture.Blazor.Server.UI.Services;
using CleanArchitecture.Blazor.Server.UI.Services.JsInterop;
using CleanArchitecture.Blazor.Server.UI.Services.Layout;
using CleanArchitecture.Blazor.Server.UI.Services.UserPreferences;
using FluentAssertions;
using Mapster;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using NUnit.Framework;
using RolesPage = CleanArchitecture.Blazor.Server.UI.Pages.Identity.Roles.Roles;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// The four role-definition write paths that live in components rather than in a service:
/// <c>RoleFormDialog</c>'s create and rename, and <c>Roles.razor</c>'s delete and import.
/// </summary>
/// <remarks>
/// <para>
/// <b>These drive the real components against a real <see cref="RoleManager{TRole}"/> over SQLite
/// and then read the role store back.</b> That is the point: role administration bypasses Mediator,
/// so there is no handler to send a command to, and a test that only asserted which buttons render
/// would prove the decoration and not the guard. Every assertion below is about what the ROLE TABLE
/// holds after the component's own write path has run.
/// </para>
/// <para>
/// <b>Why the guard cannot be proved at one chokepoint.</b> <c>RoleFormDialog</c> holds its own
/// <c>RoleManager</c> and <c>Roles.razor</c> holds another; neither goes through
/// <c>AuthorizationBehaviour</c>. So the guards are per call site, exactly as
/// <c>AdministratorProtectionService</c>'s are, and each call site needs its own evidence. The
/// re-permissioning path is the one that DOES have a service, and it is proved in
/// <c>RoleDefinitionRightTests</c> instead.
/// </para>
/// <para>
/// <b>Narrowed, not emptied.</b> Every refusal below is paired with the same operation succeeding
/// for a holder, through the same component and the same code path.
/// </para>
/// <para>
/// <b>Only rendering can see the second line.</b> The application renders at
/// <c>InteractiveServerRenderMode(prerender: false)</c>, so an HTTP response carries the shell and
/// none of the grid.
/// </para>
/// </remarks>
[TestFixture]
public class RoleDefinitionComponentTests
{
    private const string ExistingRole = "Editors";

    private BunitContext _ctx = null!;
    private SqliteConnection _connection = null!;
    private MutableUserContextAccessor _contextAccessor = null!;
    private ConfigurablePermissionQueryService _permissionQuery = null!;
    private string _actorId = null!;

    /// <summary>How many confirmation prompts the page asked for. A refusal must ask for none.</summary>
    private int _confirmationsShown;

    [TearDown]
    public async Task TearDown()
    {
        await _ctx.DisposeAsync();
        await _connection.DisposeAsync();
    }

    // ---- harness -------------------------------------------------------------------------------

    private sealed class MutableUserContextAccessor : IUserContextAccessor
    {
        public UserContext? Current { get; set; }
        public IDisposable Push(UserContext context) => throw new NotSupportedException();
        public void Clear() => Current = null;
    }

    private sealed class ConfigurablePermissionQueryService : IPermissionQueryService
    {
        public List<PermissionModel> Held { get; } = new();

        public Task<IList<PermissionModel>> GetAllPermissionsByUserId(string userId) =>
            Task.FromResult<IList<PermissionModel>>(Held);
        public Task<IList<PermissionModel>> GetAllPermissionsByRoleId(string roleId) =>
            Task.FromResult<IList<PermissionModel>>(new List<PermissionModel>());
    }

    /// <summary>
    /// Registers the real Identity stack over an in-memory SQLite database, an ambient principal,
    /// and everything the two components inject. <paramref name="mayDefineRoles"/> is the one
    /// variable: every other right is granted, so a refusal below is attributable to it alone.
    /// </summary>
    private async Task ArrangeAsync(bool mayDefineRoles)
    {
        _ctx = new BunitContext();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();
        _contextAccessor = new MutableUserContextAccessor();
        _permissionQuery = new ConfigurablePermissionQueryService();

        var services = _ctx.Services;
        services.AddLogging();
        services.AddLocalization();
        services.AddMudServices();

        services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite(_connection));
        services.AddIdentityCore<ApplicationUser>(o =>
            {
                o.Password.RequireDigit = false;
                o.Password.RequiredLength = 6;
                o.Password.RequireNonAlphanumeric = false;
                o.Password.RequireUppercase = false;
                o.Password.RequireLowercase = false;
            })
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>();

        services.AddSingleton<IUserContextAccessor>(_contextAccessor);
        services.AddSingleton<IPermissionQueryService>(_permissionQuery);
        services.AddScoped<AdministratorProtectionService>();
        services.AddScoped<PermissionAssignmentService>();

        services.AddSingleton(Mock.Of<IUserPreferencesService>());
        services.AddScoped<LayoutService>();
        services.AddScoped<DialogServiceHelper>();
        services.AddSingleton(new TypeAdapterConfig());
        services.AddSingleton(Mock.Of<IApplicationSettings>());
        services.AddSingleton(Mock.Of<IValidationService>());
        services.AddSingleton(Mock.Of<IAppCache>());
        services.AddSingleton(Mock.Of<IObjectMapper>());
        services.AddSingleton(Mock.Of<IExcelService>());
        services.AddScoped<BlazorDownloadFileService>();
        // Injected by _Imports.razor into every component. Neither path under test sends anything
        // through it - role administration bypasses Mediator, which is the whole reason these
        // guards had to go at the call sites - but the property injection still has to resolve.
        services.AddSingleton(Mock.Of<Mediator.IMediator>());

        var profileState = new Mock<IUserProfileState>();
        profileState.SetupGet(x => x.Value).Returns(UserProfile.Empty);
        services.AddSingleton(profileState.Object);

        var roleDataSource = new Mock<IDataSourceService<ApplicationRoleDto>>();
        roleDataSource.Setup(x => x.RefreshAsync()).Returns(Task.CompletedTask);
        services.AddSingleton(roleDataSource.Object);

        // A confirmation dialog that answers YES immediately, registered AFTER AddMudServices so it
        // wins. Without it DialogServiceHelper awaits `dialog.Result` on a dialog nothing renders
        // and nothing ever answers, so the delete path never returns - which is exactly what the
        // first draft of this fixture did, and it hung the test host rather than failing.
        //
        // Auto-confirming is not a shortcut around the guard: it makes the HOLDER's delete actually
        // execute, so the positive control below asserts a role that is really gone rather than a
        // prompt that was really opened. The refusals happen before the prompt is ever requested,
        // which ConfirmationsShown asserts separately.
        _confirmationsShown = 0;
        var dialogReference = new Mock<IDialogReference>();
        dialogReference.SetupGet(x => x.Result)
            .Returns(Task.FromResult<DialogResult?>(DialogResult.Ok(true)));
        var dialogService = new Mock<IDialogService>();
        dialogService.Setup(x => x.ShowAsync<ConfirmationDialog>(
                It.IsAny<string>(), It.IsAny<DialogParameters>(), It.IsAny<DialogOptions>()))
            .Callback(() => _confirmationsShown++)
            .ReturnsAsync(dialogReference.Object);
        services.AddSingleton(dialogService.Object);

        var permissions = new Mock<IPermissionService>();
        permissions.Setup(x => x.GetAccessRightsAsync<RolesAccessRights>())
            .ReturnsAsync(new RolesAccessRights
            {
                View = true, Create = true, Edit = true, Delete = true,
                Search = true, Export = true, Import = true, ManagePermissions = true,
                ManageDefinitions = mayDefineRoles
            });
        services.AddSingleton(permissions.Object);

        // Schema, one seeded role, and the acting principal.
        using var scope = services.BuildServiceProvider().CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.EnsureCreatedAsync();

        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        (await roleManager.CreateAsync(new ApplicationRole { Name = ExistingRole })).Succeeded
            .Should().BeTrue();

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var actor = new ApplicationUser { UserName = "actor", Email = "actor@example.com" };
        (await userManager.CreateAsync(actor, "Password123!")).Succeeded.Should().BeTrue();
        _actorId = actor.Id;

        _contextAccessor.Current = new UserContext(UserId: actor.Id, UserName: "actor");
        if (mayDefineRoles)
        {
            _permissionQuery.Held.Add(new PermissionModel
            {
                ClaimType = ApplicationClaimTypes.Permission,
                ClaimValue = Permissions.Roles.ManageDefinitions,
                Assigned = true,
                UserId = actor.Id
            });
        }
    }

    // The components resolve RoleManager from their OWN scope, so the store must be read from a
    // fresh scope too - reading through a stale one would show cached entities rather than rows.
    private async Task<string[]> RoleNamesAsync()
    {
        using var scope = _ctx.Services.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        return (await roleManager.Roles.Select(r => r.Name!).ToListAsync())
            .OrderBy(n => n, StringComparer.Ordinal).ToArray();
    }

    private async Task<string> ExistingRoleIdAsync()
    {
        using var scope = _ctx.Services.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        return (await roleManager.FindByNameAsync(ExistingRole))!.Id;
    }

    private IRenderedComponent<RoleFormDialog> RenderDialog(RoleFormDialog.InputModel model) =>
        _ctx.Render<RoleFormDialog>(p => p
            .Add(x => x.Model, model)
            .AddCascadingValue(Mock.Of<IMudDialogInstance>()));

    private static async Task SubmitAsync(IRenderedComponent<RoleFormDialog> dialog)
    {
        var submit = typeof(RoleFormDialog).GetMethod(
            "Submit", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await dialog.InvokeAsync(async () => await (Task)submit.Invoke(dialog.Instance, null)!);
    }

    private IRenderedComponent<RolesPage> RenderPage() => _ctx.Render<RolesPage>();

    private static Task InvokePageAsync(
        IRenderedComponent<RolesPage> page, string method, params object?[] args)
    {
        var target = typeof(RolesPage).GetMethod(
            method, BindingFlags.Instance | BindingFlags.NonPublic)!;
        return page.InvokeAsync(async () => await (Task)target.Invoke(page.Instance, args)!);
    }

    // ---- create, through RoleFormDialog --------------------------------------------------------

    [Test]
    public async Task WithoutTheRight_TheDialogCreatesNoRole()
    {
        await ArrangeAsync(mayDefineRoles: false);
        var dialog = RenderDialog(new RoleFormDialog.InputModel { Name = "Auditors" });

        await SubmitAsync(dialog);

        (await RoleNamesAsync()).Should().BeEquivalentTo(new[] { ExistingRole },
            "the role must not exist - the refusal is the guard, not the missing button");
    }

    [Test]
    public async Task WithTheRight_TheDialogCreatesTheRole()
    {
        await ArrangeAsync(mayDefineRoles: true);
        var dialog = RenderDialog(new RoleFormDialog.InputModel { Name = "Auditors" });

        await SubmitAsync(dialog);

        (await RoleNamesAsync()).Should().BeEquivalentTo(new[] { ExistingRole, "Auditors" });
    }

    // ---- rename, through RoleFormDialog --------------------------------------------------------

    [Test]
    public async Task WithoutTheRight_TheDialogDoesNotRenameAnExistingRole()
    {
        // The concrete harm Pass 32 §4.2 named: renaming a role every other tenant relies on.
        await ArrangeAsync(mayDefineRoles: false);
        var dialog = RenderDialog(new RoleFormDialog.InputModel
        {
            Id = await ExistingRoleIdAsync(),
            Name = "Renamed",
            Description = "changed"
        });

        await SubmitAsync(dialog);

        (await RoleNamesAsync()).Should().Equal(ExistingRole);
    }

    [Test]
    public async Task WithTheRight_TheDialogRenamesTheRole()
    {
        await ArrangeAsync(mayDefineRoles: true);
        var dialog = RenderDialog(new RoleFormDialog.InputModel
        {
            Id = await ExistingRoleIdAsync(),
            Name = "Renamed",
            Description = "changed"
        });

        await SubmitAsync(dialog);

        (await RoleNamesAsync()).Should().Equal("Renamed");
    }

    // ---- import, through Roles.razor -----------------------------------------------------------

    [Test]
    public async Task WithoutTheRight_TheImportPathCreatesNoRole()
    {
        await ArrangeAsync(mayDefineRoles: false);
        var page = RenderPage();
        var imported = new List<ApplicationRole>
        {
            new("Imported-A"), new("Imported-B")
        };

        var act = async () => await InvokePageAsync(page, "ProcessImportedRolesAsync", imported);

        await act.Should().ThrowAsync<ForbiddenAccessException>();
        (await RoleNamesAsync()).Should().BeEquivalentTo(new[] { ExistingRole },
            "the import is the only path that creates several roles at once; it must create none");
    }

    [Test]
    public async Task WithTheRight_TheImportPathCreatesTheRoles()
    {
        await ArrangeAsync(mayDefineRoles: true);
        var page = RenderPage();
        var imported = new List<ApplicationRole>
        {
            new("Imported-A"), new("Imported-B")
        };

        await InvokePageAsync(page, "ProcessImportedRolesAsync", imported);

        (await RoleNamesAsync()).Should()
            .BeEquivalentTo(new[] { ExistingRole, "Imported-A", "Imported-B" });
    }

    // ---- delete, through Roles.razor -----------------------------------------------------------

    [Test]
    public async Task WithoutTheRight_TheSingleDeletePathDeletesNothing()
    {
        await ArrangeAsync(mayDefineRoles: false);
        var page = RenderPage();
        var dto = new ApplicationRoleDto { Id = await ExistingRoleIdAsync(), Name = ExistingRole };

        await InvokePageAsync(page, "OnDelete", dto);

        (await RoleNamesAsync()).Should().BeEquivalentTo(new[] { ExistingRole },
            "the role survives even though the confirmation would have answered yes");
        _confirmationsShown.Should().Be(0,
            "the refusal happens BEFORE the prompt, so the caller is told immediately rather than " +
            "after agreeing to a deletion that cannot happen");
    }

    [Test]
    public async Task WithTheRight_TheSingleDeletePathDeletesTheRole()
    {
        await ArrangeAsync(mayDefineRoles: true);
        var page = RenderPage();
        var dto = new ApplicationRoleDto { Id = await ExistingRoleIdAsync(), Name = ExistingRole };

        await InvokePageAsync(page, "OnDelete", dto);

        _confirmationsShown.Should().Be(1);
        (await RoleNamesAsync()).Should().BeEmpty("the holder's delete really happened");
    }

    [Test]
    public async Task WithoutTheRight_TheBulkDeletePathDeletesNothing()
    {
        await ArrangeAsync(mayDefineRoles: false);
        var page = RenderPage();
        SetSelectedRoles(page, new ApplicationRoleDto { Id = await ExistingRoleIdAsync(), Name = ExistingRole });

        await InvokePageAsync(page, "OnDeleteChecked");

        (await RoleNamesAsync()).Should().BeEquivalentTo(new[] { ExistingRole });
        _confirmationsShown.Should().Be(0);
    }

    [Test]
    public async Task WithTheRight_TheBulkDeletePathDeletesTheSelection()
    {
        await ArrangeAsync(mayDefineRoles: true);
        var page = RenderPage();
        SetSelectedRoles(page, new ApplicationRoleDto { Id = await ExistingRoleIdAsync(), Name = ExistingRole });

        await InvokePageAsync(page, "OnDeleteChecked");

        _confirmationsShown.Should().Be(1);
        (await RoleNamesAsync()).Should().BeEmpty();
    }

    [Test]
    public async Task AHolderIsStillRefusedOnTheProtectedAdministratorRole()
    {
        // The two guarantees kept apart: holding ManageDefinitions does not make the Admin role
        // deletable, because AdministratorProtectionService is a different rule and still runs.
        await ArrangeAsync(mayDefineRoles: true);
        using (var scope = _ctx.Services.CreateScope())
        {
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            (await roleManager.CreateAsync(new ApplicationRole
            {
                Name = AdministratorProtectionService.AdministratorRole
            })).Succeeded.Should().BeTrue();
        }

        var page = RenderPage();
        var adminRoleId = await AdminRoleIdAsync();
        var dto = new ApplicationRoleDto
        {
            Id = adminRoleId,
            Name = AdministratorProtectionService.AdministratorRole
        };

        await InvokePageAsync(page, "OnDelete", dto);

        (await RoleNamesAsync()).Should()
            .Contain(AdministratorProtectionService.AdministratorRole);
    }

    private async Task<string> AdminRoleIdAsync()
    {
        using var scope = _ctx.Services.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        return (await roleManager.FindByNameAsync(
            AdministratorProtectionService.AdministratorRole))!.Id;
    }

    private static void SetSelectedRoles(IRenderedComponent<RolesPage> page, params ApplicationRoleDto[] roles)
    {
        var field = typeof(RolesPage).GetField(
            "_selectedRoles", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(page.Instance, new HashSet<ApplicationRoleDto>(roles));
    }

    // ---- the second line: what the page offers -------------------------------------------------

    [Test]
    public async Task WithoutTheRight_ThePageSaysWhyRatherThanShowingAnEmptyToolbar()
    {
        await ArrangeAsync(mayDefineRoles: false);

        var markup = RenderPage().Markup;

        markup.Should().Contain("manage role definitions",
            "a grid of roles with no way to edit any of them reads as a bug unless it says why");
        markup.Should().Contain("Assigning users to these roles does not",
            "the thing they CAN still do is the part they would otherwise look for in the wrong place");
    }

    [Test]
    public async Task WithTheRight_ThePageShowsNoSuchNotice()
    {
        await ArrangeAsync(mayDefineRoles: true);

        RenderPage().Markup.Should().NotContain("manage role definitions");
    }

    [Test]
    public async Task WithoutTheRight_TheRowMenuIsReplacedByTheNotAllowedButton()
    {
        // Pass 32 A3 checked, not assumed: this grid is NOT DataGridEditMode.Cell, so its
        // CellTemplate IS reached and a decoration written there is visible. On the picklist grid
        // the same code would have been unreachable.
        await ArrangeAsync(mayDefineRoles: false);

        RenderPage().Markup.Should().Contain(AppStrings.NoAllowed);
    }
}
