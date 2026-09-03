#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Mappings;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Application.Features.Identity.DTOs;
using CleanArchitecture.Blazor.Application.Features.Tenants.DTOs;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Configurations;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Services.Identity;
using CleanArchitecture.Blazor.Server.UI.Pages.Identity.Users;
using CleanArchitecture.Blazor.Server.UI.Services;
using CleanArchitecture.Blazor.Server.UI.Services.JsInterop;
using CleanArchitecture.Blazor.Server.UI.Services.Layout;
using CleanArchitecture.Blazor.Server.UI.Services.UserPreferences;
using FluentAssertions;
using Mapster;
using Mediator;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor.Services;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// Whether the active/inactive toggle on the users grid is reachable without
/// <c>Permissions.Users.Deactivation</c>.
/// </summary>
/// <remarks>
/// <b>This can only be seen by rendering.</b> The permission gated nothing at all: the toggle was
/// drawn for any holder of <c>Users.View</c>, so the constant was granted to the administrator,
/// listed in the role editor as a revocable right, and enforced nowhere. Revoking it changed
/// nothing - a false statement about what the system enforces.
/// <para>
/// An HTTP test cannot see this. The app renders at
/// <c>InteractiveServerRenderMode(prerender: false)</c>, so a response carries the shell and none of
/// the grid; the same reason Pass 16A's empty Security tab shipped green. This renders the real page
/// against a real <c>UserManager</c> over SQLite and inspects the cell that is actually produced.
/// </para>
/// <para>
/// <b>The status is still SHOWN without the permission</b> - seeing whether an account is active is
/// part of viewing users. It simply stops being clickable, which is why these tests assert on the
/// control being disabled rather than on the presence of a checkbox.
/// </para>
/// </remarks>
[TestFixture]
public class UserDeactivationPermissionComponentTests
{
    private const string TenantId = "tenant-a";

    private BunitContext _ctx = null!;
    private SqliteConnection _connection = null!;

    [TearDown]
    public async Task TearDown()
    {
        await _ctx.DisposeAsync();
        await _connection.DisposeAsync();
    }

    /// <summary>Boots the page with a principal holding exactly the access rights given.</summary>
    private async Task ArrangeAsync(bool canDeactivate)
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        _ctx = new BunitContext();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var services = _ctx.Services;
        services.AddLogging();
        services.AddLocalization();
        services.AddMudServices();

        // The Identity stack goes into the SAME container bUnit renders from. Users derives from
        // OwningComponentBase and resolves UserManager, RoleManager and the two protection services
        // through ScopedServices - which is a scope of the component's own container, not of some
        // provider handed to it. Registering them elsewhere and injecting an IServiceScopeFactory
        // does not work: the container supplies its own, and the page fails on the first
        // GetRequiredService.
        //
        // Real, not stubbed: the grid's ServerData calls UserManager.Users, so a stub would render
        // no rows and these tests would pass without ever drawing the cell under test.
        services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite(_connection));
        services.AddIdentityCore<ApplicationUser>()
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>();
        services.AddSingleton(_connection);
        services.AddScoped<IApplicationDbContextFactory, TestDbContextFactory>();
        services.AddScoped<PermissionAssignmentService>();
        services.AddScoped<AdministratorProtectionService>();

        // Constructor dependencies of PermissionAssignmentService, which the page resolves eagerly
        // in InitializeServices. Nothing here participates in what is under test - the page must
        // simply be able to start.
        services.AddSingleton(Mock.Of<IPermissionQueryService>());
        services.AddSingleton(Mock.Of<IUserContextAccessor>());

        services.AddSingleton(Mock.Of<IUserPreferencesService>());
        services.AddScoped<LayoutService>();
        services.AddScoped<DialogServiceHelper>();
        // The REAL mapping configuration, not a bare TypeAdapterConfig: the grid projects
        // ApplicationUser to ApplicationUserDto, and the ApplicationUser -> Superior relation is
        // self-referential, so an unconfigured Mapster refuses it as a circular reference. This is
        // the same configuration the running application registers.
        services.AddSingleton(MapsterConfiguration.Create());

        services.AddSingleton(Mock.Of<IApplicationSettings>());
        services.AddSingleton(Mock.Of<IUserProfileState>());
        services.AddSingleton(Mock.Of<IValidationService>());
        services.AddSingleton(Mock.Of<IMediator>());
        services.AddSingleton(Mock.Of<IAppCache>());
        services.AddSingleton(Mock.Of<IObjectMapper>());
        services.AddSingleton(Mock.Of<IExcelService>());
        services.AddSingleton(new BlazorDownloadFileService(_ctx.JSInterop.JSRuntime));

        // The one thing under test.
        var permissions = new Mock<IPermissionService>();
        permissions.Setup(x => x.GetAccessRightsAsync<UsersAccessRights>())
            .ReturnsAsync(new UsersAccessRights { View = true, Deactivation = canDeactivate });
        services.AddSingleton(permissions.Object);

        services.AddSingleton(DataSource<ApplicationUserDto>());
        services.AddSingleton(DataSource<ApplicationRoleDto>());
        services.AddSingleton(DataSource(new TenantDto { Id = TenantId, Name = "Tenant A" }));

        // Seeded LAST: resolving a service builds the container, after which nothing more may be
        // registered.
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.Tenants.Add(new Tenant { Id = TenantId, Name = "Tenant A" });
        db.Users.Add(new ApplicationUser
        {
            Id = "target", UserName = "target", Email = "target@x.com",
            TenantId = TenantId, IsActive = true, DisplayName = "Target User"
        });
        await db.SaveChangesAsync();
    }

    /// <summary>An empty data source of the given type - the page only needs it to initialise.</summary>
    private static IDataSourceService<T> DataSource<T>(params T[] items)
    {
        var mock = new Mock<IDataSourceService<T>>();
        mock.Setup(x => x.InitializeAsync()).Returns(Task.CompletedTask);
        mock.SetupGet(x => x.DataSource).Returns(items);
        return mock.Object;
    }

    /// <summary>Creates contexts over the one open in-memory connection.</summary>
    private sealed class TestDbContextFactory : IApplicationDbContextFactory
    {
        private readonly SqliteConnection _connection;
        public TestDbContextFactory(SqliteConnection connection) => _connection = connection;

        public ValueTask<IApplicationDbContext> CreateAsync(CancellationToken ct = default) =>
            new(new ApplicationDbContext(
                new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options));
    }


    // ---- the gate --------------------------------------------------------------------------------
    [Test]
    public async Task WithoutTheDeactivationPermission_TheToggleIsNotClickable()
    {
        // RED before Pass 25: 0 disabled controls - the cell drew the same interactive checkbox for
        // everyone, because nothing consulted the permission.
        await ArrangeAsync(canDeactivate: false);

        var page = RenderWithTheUserRow();

        DisabledCheckboxes(page).Should().Be(1,
            "the active-status control is rendered disabled for a holder of Users.View alone");
    }

    [Test]
    public async Task WithTheDeactivationPermission_TheToggleIsClickable()
    {
        await ArrangeAsync(canDeactivate: true);

        var page = RenderWithTheUserRow();

        DisabledCheckboxes(page).Should().Be(0,
            "the permission is what the capability is named after");
    }

    [Test]
    public async Task TheStatusIsStillVisibleWithoutThePermission()
    {
        // The gate removes the ACTION, not the information. An operator asking "why can this person
        // not sign in?" needs to see that the account is inactive whether or not they may change it,
        // which is why the gated branch renders a DISABLED control rather than nothing at all.
        await ArrangeAsync(canDeactivate: false);
        var gated = RenderWithTheUserRow();

        CheckboxCount(gated).Should().Be(CheckboxCountWithPermission,
            "the same controls are drawn either way - one of them simply cannot be operated");
    }

    [Test]
    public async Task TheControlCountIsTheSameWithThePermission()
    {
        // Pins the constant above, so the comparison in the previous test cannot silently start
        // measuring against a number that no longer describes this page.
        await ArrangeAsync(canDeactivate: true);
        var page = RenderWithTheUserRow();

        CheckboxCount(page).Should().Be(CheckboxCountWithPermission);
    }

    /// <summary>
    /// The number of checkboxes this page draws for one user row: select-all, row-selection, and the
    /// active-status control under test.
    /// </summary>
    private const int CheckboxCountWithPermission = 3;

    /// <summary>Renders the page and waits until the seeded user's row is actually in the DOM.</summary>
    /// <remarks>
    /// The grid loads through <c>ServerData</c>, so the first render carries headers and no rows.
    /// Asserting before the row arrives would measure an empty table - which satisfies "nothing is
    /// clickable" for entirely the wrong reason.
    /// </remarks>
    private IRenderedComponent<Users> RenderWithTheUserRow()
    {
        var page = _ctx.Render<Users>();
        page.WaitForState(
            () => page.Markup.Contains("target", StringComparison.Ordinal),
            TimeSpan.FromSeconds(10));
        return page;
    }

    private static int CheckboxCount(IRenderedComponent<Users> page) =>
        page.FindAll("input[type=checkbox]").Count;

    /// <summary>
    /// How many rendered checkboxes cannot be operated.
    /// </summary>
    /// <remarks>
    /// MudBlazor renders a disabled checkbox with the <c>disabled</c> attribute and an enabled one
    /// without it. <b>Counting matters rather than "is any enabled":</b> the grid also draws a
    /// select-all checkbox and a row-selection checkbox, both always enabled, so "some enabled
    /// control exists" is true on both pages and would have made these tests pass in every state.
    /// That was the first version of this helper, and it did.
    /// </remarks>
    private static int DisabledCheckboxes(IRenderedComponent<Users> page) =>
        page.FindAll("input[type=checkbox]").Count(input => input.HasAttribute("disabled"));
}
#nullable restore
