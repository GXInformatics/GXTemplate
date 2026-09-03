#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
/// The users grid and the user export, bounded to the tenants the principal may see.
/// </summary>
/// <remarks>
/// Before Pass 27 neither was bounded: <c>CreateSearchPredicate</c>'s tenant clause was
/// <c>(string.IsNullOrEmpty(_selectedTenantId) || x.TenantId == _selectedTenantId)</c> with
/// <c>_selectedTenantId</c> defaulting to empty, so any holder of <c>Permissions.Users.View</c>
/// could list every user in every tenant - and download them, with email and phone number, through
/// an export that reuses the same predicate.
/// <para>
/// <b>The export is asserted separately from the grid, on purpose.</b> They share a predicate today,
/// so a test that read only the grid would pass while the export leaked if the two were ever
/// separated - and the export is the surface a partial fix misses, because it has no visible rows to
/// notice.
/// </para>
/// <para>
/// Only rendering can see either. The app runs at <c>InteractiveServerRenderMode(prerender: false)</c>,
/// so an HTTP response carries the shell and no grid at all.
/// </para>
/// </remarks>
[TestFixture]
public class UserTenantScopeComponentTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

    private BunitContext _ctx = null!;
    private SqliteConnection _connection = null!;
    private List<ApplicationUserDto> _exported = new();

    [TearDown]
    public async Task TearDown()
    {
        await _ctx.DisposeAsync();
        await _connection.DisposeAsync();
    }

    /// <param name="allowedTenantIds">null means no ambient principal at all.</param>
    private async Task ArrangeAsync(string[]? allowedTenantIds, bool viewAllTenants = false)
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();
        _exported = new List<ApplicationUserDto>();

        _ctx = new BunitContext();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var services = _ctx.Services;
        services.AddLogging();
        services.AddLocalization();
        services.AddMudServices();

        services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite(_connection));
        services.AddIdentityCore<ApplicationUser>()
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>();
        services.AddSingleton(_connection);
        services.AddScoped<IApplicationDbContextFactory, ScopeTestDbContextFactory>();
        services.AddScoped<PermissionAssignmentService>();
        services.AddScoped<AdministratorProtectionService>();
        services.AddSingleton(Mock.Of<IPermissionQueryService>());

        services.AddSingleton(Mock.Of<IUserPreferencesService>());
        services.AddScoped<LayoutService>();
        services.AddScoped<DialogServiceHelper>();
        services.AddSingleton(MapsterConfiguration.Create());

        services.AddSingleton(Mock.Of<IApplicationSettings>());
        services.AddSingleton(Mock.Of<IUserProfileState>());
        services.AddSingleton(Mock.Of<IValidationService>());
        services.AddSingleton(Mock.Of<IMediator>());
        services.AddSingleton(Mock.Of<IAppCache>());
        services.AddSingleton(Mock.Of<IObjectMapper>());
        services.AddSingleton(new BlazorDownloadFileService(_ctx.JSInterop.JSRuntime));

        // Captures exactly the rows the spreadsheet would contain, which is what "the export leaks"
        // means. Asserting on the produced bytes would prove less and read worse.
        var excel = new Mock<IExcelService>();
        excel.Setup(x => x.ExportAsync(
                It.IsAny<IEnumerable<ApplicationUserDto>>(),
                It.IsAny<Dictionary<string, Func<ApplicationUserDto, object?>>>(),
                It.IsAny<string>()))
            .Returns((IEnumerable<ApplicationUserDto> rows,
                Dictionary<string, Func<ApplicationUserDto, object?>> _, string __) =>
            {
                _exported = rows.ToList();
                return Task.FromResult(Array.Empty<byte>());
            });
        services.AddSingleton(excel.Object);

        var permissions = new Mock<IPermissionService>();
        permissions.Setup(x => x.GetAccessRightsAsync<UsersAccessRights>())
            .ReturnsAsync(new UsersAccessRights
            {
                View = true, Export = true, ViewAllTenants = viewAllTenants
            });
        services.AddSingleton(permissions.Object);

        // The ambient principal the page bounds its rows by.
        var userContext = new Mock<IUserContextAccessor>();
        userContext.SetupGet(x => x.Current).Returns(allowedTenantIds is null
            ? null
            : new UserContext("admin-a", "admin-a",
                TenantId: allowedTenantIds.FirstOrDefault(), AllowedTenantIds: allowedTenantIds));
        services.AddSingleton(userContext.Object);

        services.AddSingleton(EmptySource<ApplicationUserDto>());
        services.AddSingleton(EmptySource<ApplicationRoleDto>());
        services.AddSingleton(EmptySource<TenantDto>());

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.Tenants.Add(new Tenant { Id = TenantA, Name = "Tenant A" });
        db.Tenants.Add(new Tenant { Id = TenantB, Name = "Tenant B" });
        db.Users.Add(NewUser("a-one", TenantA));
        db.Users.Add(NewUser("a-two", TenantA));
        db.Users.Add(NewUser("b-one", TenantB));
        db.Users.Add(NewUser("orphan", null));
        await db.SaveChangesAsync();
    }

    private static ApplicationUser NewUser(string name, string? tenantId) => new()
    {
        Id = name, UserName = name, Email = $"{name}@x.com", DisplayName = name,
        TenantId = tenantId, IsActive = true
    };

    private static IDataSourceService<T> EmptySource<T>()
    {
        var mock = new Mock<IDataSourceService<T>>();
        mock.Setup(x => x.InitializeAsync()).Returns(Task.CompletedTask);
        mock.SetupGet(x => x.DataSource).Returns(Array.Empty<T>());
        return mock.Object;
    }

    private sealed class ScopeTestDbContextFactory : IApplicationDbContextFactory
    {
        private readonly SqliteConnection _connection;
        public ScopeTestDbContextFactory(SqliteConnection connection) => _connection = connection;

        public ValueTask<IApplicationDbContext> CreateAsync(CancellationToken ct = default) =>
            new(new ApplicationDbContext(
                new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options));
    }

    // ---- the two surfaces --------------------------------------------------------------------------

    /// <summary>Renders the page and returns the usernames the grid actually drew.</summary>
    private string[] GridUserNames()
    {
        var page = _ctx.Render<Users>();

        // The grid loads through ServerData, so the first render has headers and no rows. Waiting on
        // a marker that is present in EVERY arrangement would be wrong here - some of these cases
        // legitimately render nothing - so this waits for the load to settle instead.
        page.WaitForState(() => !page.Markup.Contains("Loading", StringComparison.Ordinal),
            TimeSpan.FromSeconds(10));

        return new[] { "a-one", "a-two", "b-one", "orphan" }
            .Where(name => page.Markup.Contains($">{name}<", StringComparison.Ordinal))
            .ToArray();
    }

    /// <summary>
    /// Runs the real export and returns the tenant ids of the rows it produced.
    /// </summary>
    /// <remarks>
    /// Invoked by reflection because <c>ExportUsersAsync</c> is private and the page offers no other
    /// entry point. That is deliberate rather than lazy: replaying the export's query in the test
    /// would assert on a COPY of the predicate, and the property under test is precisely that the
    /// export and the grid share one. This runs the real method, over the real predicate, against
    /// the real database.
    /// </remarks>
    private async Task<string[]> ExportedUserNamesAsync()
    {
        var page = _ctx.Render<Users>();
        page.WaitForState(() => !page.Markup.Contains("Loading", StringComparison.Ordinal),
            TimeSpan.FromSeconds(10));

        var export = typeof(Users).GetMethod("ExportUsersAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        await page.InvokeAsync(async () => await (Task)export.Invoke(page.Instance, null)!);

        return _exported.Select(u => u.UserName).OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    // ---- E.2: a tenant-A administrator ---------------------------------------------------------------

    [Test]
    public async Task TheGrid_ShowsNoOtherTenantsUsers()
    {
        // RED before Pass 27: a-one, a-two, b-one and orphan.
        await ArrangeAsync(new[] { TenantA });

        GridUserNames().Should().BeEquivalentTo("a-one", "a-two");
    }

    [Test]
    public async Task TheExport_ContainsNoOtherTenantsRows()
    {
        // Asserted separately from the grid, per Pass 23 §6.3: the export is the surface a partial
        // fix misses, because nothing about it is visible until somebody opens the spreadsheet.
        await ArrangeAsync(new[] { TenantA });

        (await ExportedUserNamesAsync()).Should().BeEquivalentTo("a-one", "a-two");
    }

    [Test]
    public async Task TheGridAndTheExport_ReturnTheSameRows()
    {
        // The property that makes this one change rather than two. If somebody later gives the
        // export its own query, this is what fails.
        await ArrangeAsync(new[] { TenantA });

        var exported = await ExportedUserNamesAsync();
        var shown = GridUserNames();

        exported.Should().BeEquivalentTo(shown);
    }

    // ---- E.5: narrowed, not emptied ------------------------------------------------------------------

    [Test]
    public async Task TheAdministratorStillSeesEveryUserInTheirOwnTenant()
    {
        // The control that stops a predicate returning nothing from passing every test above. Two
        // users share tenant A and BOTH must be listed.
        await ArrangeAsync(new[] { TenantA });

        GridUserNames().Should().Contain("a-one").And.Contain("a-two");
    }

    [Test]
    public async Task APrincipalInTwoTenants_SeesBoth()
    {
        // AllowedTenantIds is a set, not a single tenant - Pass 25 made it the union of membership
        // and the principal's own tenant. A bound that only ever honoured one entry would satisfy
        // every other test here.
        await ArrangeAsync(new[] { TenantA, TenantB });

        GridUserNames().Should().BeEquivalentTo("a-one", "a-two", "b-one");
    }

    // ---- E.3: the escape ------------------------------------------------------------------------------

    [Test]
    public async Task ACrossTenantHolder_SeesEveryTenantInTheGrid()
    {
        await ArrangeAsync(new[] { TenantA }, viewAllTenants: true);

        // Including the tenantless user, who belongs to nobody and is therefore visible only here.
        GridUserNames().Should().BeEquivalentTo("a-one", "a-two", "b-one", "orphan");
    }

    [Test]
    public async Task ACrossTenantHolder_ExportsEveryTenant()
    {
        await ArrangeAsync(new[] { TenantA }, viewAllTenants: true);

        (await ExportedUserNamesAsync()).Should().BeEquivalentTo("a-one", "a-two", "b-one", "orphan");
    }

    // ---- E.4: fail closed -----------------------------------------------------------------------------

    [Test]
    public async Task WithNoAmbientPrincipal_TheGridIsEmpty()
    {
        // Asserted, not assumed. The failure mode of an isolation predicate must be an empty grid,
        // never an unfiltered one.
        await ArrangeAsync(allowedTenantIds: null);

        GridUserNames().Should().BeEmpty();
    }

    [Test]
    public async Task WithNoAmbientPrincipal_TheExportIsEmpty()
    {
        await ArrangeAsync(allowedTenantIds: null);

        (await ExportedUserNamesAsync()).Should().BeEmpty();
    }

    [Test]
    public async Task WithAnEmptyAllowedSet_TheGridIsEmpty()
    {
        await ArrangeAsync(Array.Empty<string>());

        GridUserNames().Should().BeEmpty();
    }

    [Test]
    public async Task ATenantlessUserIsVisibleOnlyToACrossTenantHolder()
    {
        // Falls out of failing closed rather than being a separate rule, and is asserted so it
        // cannot change silently: a user belonging to no tenant matches no allowed id.
        await ArrangeAsync(new[] { TenantA, TenantB });

        GridUserNames().Should().NotContain("orphan");
    }
}
#nullable restore
