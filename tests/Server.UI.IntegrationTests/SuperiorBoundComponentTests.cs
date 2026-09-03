#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Storage;
using CleanArchitecture.Blazor.Application.Common.Mappings;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Application.Features.Identity.DTOs;
using CleanArchitecture.Blazor.Application.Features.Tenants.DTOs;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Configurations;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Services.Identity;
using CleanArchitecture.Blazor.Server.UI.Components.Inputs.Autocomplete;
using CleanArchitecture.Blazor.Server.UI.Pages.Identity.Users.Components;
using CleanArchitecture.Blazor.Server.UI.Services;
using CleanArchitecture.Blazor.Server.UI.Services.Layout;
using CleanArchitecture.Blazor.Server.UI.Services.UserPreferences;
using FluentAssertions;
using Mediator;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// The superior picker is bounded by the edited user's ACTUAL primary tenant.
/// </summary>
/// <remarks>
/// Pass 27 A3: the dialog's model carried a tenant SET with no primary-tenant concept, so the bound
/// fell back to whichever tenant sorted first in that set. For a single-tenant user that is the same
/// thing; for a multi-tenant user it could be a different tenant of their own - safe, because it is
/// never wider than the user's own tenants, but wrong enough to show the wrong colleagues.
/// <para>
/// Pass 28 gave <c>InputModel</c> a read-only <c>TenantId</c> and passes it through
/// <c>PrimaryTenantRule</c>, which keeps an existing primary while it is still selected. The save
/// path still re-derives from the database row - this field is context, never a source of truth.
/// </para>
/// </remarks>
[TestFixture]
public class SuperiorBoundComponentTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

    private BunitContext _ctx = null!;
    private SqliteConnection _connection = null!;

    [TearDown]
    public async Task TearDown()
    {
        await _ctx.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private async Task ArrangeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

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
        services.AddScoped<IApplicationDbContextFactory, BoundTestDbContextFactory>();
        services.AddScoped<AdministratorProtectionService>();
        services.AddSingleton(Mock.Of<IUserContextLoader>());

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
        services.AddSingleton(Mock.Of<IPermissionService>());
        services.AddSingleton(Mock.Of<IFileStorage>());

        services.AddSingleton(TenantSource());
        services.AddSingleton(UserSource());

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    private static IDataSourceService<TenantDto> TenantSource()
    {
        var mock = new Mock<IDataSourceService<TenantDto>>();
        mock.Setup(x => x.InitializeAsync()).Returns(Task.CompletedTask);
        mock.SetupGet(x => x.DataSource).Returns(new[]
        {
            new TenantDto { Id = TenantA, Name = "Tenant A" },
            new TenantDto { Id = TenantB, Name = "Tenant B" }
        });
        return mock.Object;
    }

    private static IDataSourceService<ApplicationUserDto> UserSource()
    {
        var mock = new Mock<IDataSourceService<ApplicationUserDto>>();
        mock.Setup(x => x.InitializeAsync()).Returns(Task.CompletedTask);
        mock.SetupGet(x => x.DataSource).Returns(Array.Empty<ApplicationUserDto>());
        return mock.Object;
    }

    private sealed class BoundTestDbContextFactory : IApplicationDbContextFactory
    {
        private readonly SqliteConnection _connection;
        public BoundTestDbContextFactory(SqliteConnection connection) => _connection = connection;

        public ValueTask<IApplicationDbContext> CreateAsync(CancellationToken ct = default) =>
            new(new ApplicationDbContext(
                new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options));
    }

    /// <summary>Shows the dialog and reports the tenant the superior picker was bounded by.</summary>
    /// <remarks>
    /// Through <c>MudDialogProvider</c> and <c>IDialogService</c>, not by rendering
    /// <c>UserFormDialog</c> directly: <c>MudDialog</c> hands its content to the dialog instance
    /// rather than rendering it inline, so a direct render produces a component whose body - and
    /// therefore the picker under test - is never in the render tree at all.
    /// </remarks>
    private async Task<string?> BoundTenantOfAsync(UserFormDialog.InputModel model)
    {
        var provider = _ctx.Render<MudDialogProvider>();
        var dialogService = _ctx.Services.GetRequiredService<IDialogService>();

        var parameters = new DialogParameters<UserFormDialog>
        {
            { x => x.Model, model },
            { x => x.UserProfile, UserProfile.Empty }
        };

        await _ctx.Renderer.Dispatcher.InvokeAsync(async () =>
            await dialogService.ShowAsync<UserFormDialog>("Edit the user", parameters));

        return provider.FindComponent<PickSuperiorAutocomplete<ApplicationUserDto>>().Instance.TenantId;
    }

    private static UserFormDialog.InputModel Model(string? primary, params string[] selected) => new()
    {
        Id = "u1",
        UserName = "u1",
        Email = "u1@x.com",
        Provider = "Local",
        TenantId = primary,
        Tenants = selected.Select(id => new TenantDto { Id = id, Name = id }).ToList()
    };

    // ---- A3 --------------------------------------------------------------------------------------

    [Test]
    public async Task TheBoundIsTheUsersActualPrimaryTenant_NotTheFirstSelected()
    {
        // RED before Pass 28: "tenant-a", the first of the set. The user's primary is B.
        await ArrangeAsync();

        (await BoundTenantOfAsync(Model(primary: TenantB, TenantA, TenantB))).Should().Be(TenantB);
    }

    [Test]
    public async Task ANewUserIsBoundByTheFirstTenantSelected()
    {
        // No primary yet, so PrimaryTenantRule falls back to the first selected - which is exactly
        // what that user's primary will become when the form is saved.
        await ArrangeAsync();

        (await BoundTenantOfAsync(Model(primary: null, TenantA, TenantB))).Should().Be(TenantA);
    }

    [Test]
    public async Task WhenThePrimaryIsDeselected_TheBoundMovesToOneThatRemains()
    {
        // The rule keeps a primary only while it is still selected - the same behaviour the save
        // path applies, so the picker never offers colleagues from a tenant the user is about to
        // leave.
        await ArrangeAsync();

        (await BoundTenantOfAsync(Model(primary: TenantA, TenantB))).Should().Be(TenantB);
    }

    [Test]
    public async Task WithNoTenantsSelected_ThePickerIsBoundedByNothing()
    {
        // Which the component treats as "search nothing" (Pass 27's fail-closed default), rather
        // than as "search everything".
        await ArrangeAsync();

        (await BoundTenantOfAsync(Model(primary: null))).Should().BeNull();
    }
}
#nullable restore
