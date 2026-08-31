#nullable enable
using System;
using System.Security.Claims;
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using Bunit.TestDoubles;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Server.UI.Pages.Identity.Login;
using CleanArchitecture.Blazor.Server.UI.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using CleanArchitecture.Blazor.Server.UI.Services.Layout;
using CleanArchitecture.Blazor.Server.UI.Services.UserPreferences;
using Mapster;
using Mediator;
using Moq;
using MudBlazor.Services;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// Where the change-password page sends the browser after a successful change.
/// </summary>
/// <remarks>
/// The HTTP tests in <see cref="ForcedPasswordChangeTests"/> prove the refresh endpoint drops the
/// claim, but they reach it by requesting it themselves. They would all still pass if this page went
/// back to navigating to "/" — which is precisely the bug Pass 17 fixed, and precisely the Pass 10
/// lesson: a navigation decided inside the circuit never becomes an HTTP request, so no HTTP test
/// can see it.
/// <para>
/// This renders the real page against a real Identity store and drives the real form.
/// </para>
/// </remarks>
[TestFixture]
public class ChangePasswordNavigationComponentTests
{
    private const string CurrentPassword = "Gx-Harness-Password-1!";
    private const string ChosenPassword = "Gx-Circuit-Chosen-9!";
    private const string UserName = "circuit-user";

    private BunitContext _ctx = null!;
    private string _userId = null!;
    private SqliteConnection _connection = null!;

    [SetUp]
    public async Task SetUp()
    {
        _ctx = new BunitContext();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        var services = _ctx.Services;
        services.AddLogging();
        services.AddLocalization();
        services.AddMudServices();
        services.AddScoped<DialogServiceHelper>();

        // The solution's root _Imports.razor injects these into EVERY component, so rendering any
        // page needs them present. Only the Identity store is real; the rest exist so construction
        // succeeds - see AuthLayoutComponentTests, which does the same.
        services.AddSingleton(new TypeAdapterConfig());
        services.AddSingleton(Mock.Of<IApplicationSettings>());
        services.AddSingleton(Mock.Of<IUserProfileState>());
        services.AddSingleton(Mock.Of<IValidationService>());
        services.AddSingleton(Mock.Of<IMediator>());
        services.AddSingleton(Mock.Of<IAppCache>());
        services.AddSingleton(Mock.Of<IPermissionService>());
        services.AddSingleton(Mock.Of<IObjectMapper>());
        services.AddSingleton(Mock.Of<IUserPreferencesService>());
        services.AddScoped<LayoutService>();

        services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite(_connection));
        services.AddIdentityCore<ApplicationUser>()
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>();

        var provider = services.BuildServiceProvider();
        await using (var db = provider.GetRequiredService<ApplicationDbContext>())
        {
            await db.Database.EnsureCreatedAsync();
        }

        using (var scope = provider.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var created = new ApplicationUser { UserName = UserName, Email = "circuit@example.com", MustChangePassword = true };
            await users.CreateAsync(created, CurrentPassword);
            _userId = created.Id;
        }

        // UserManager.GetUserAsync resolves by the NameIdentifier claim, not the name - without it
        // the page takes its "session expired" branch and navigates to the login page instead.
        _ctx.AddAuthorization()
            .SetAuthorized(UserName)
            .SetClaims(new Claim(ClaimTypes.NameIdentifier, _userId));
    }

    [TearDown]
    public async Task TearDown()
    {
        await _ctx.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private void Fill(IRenderedComponent<ChangePassword> page)
    {
        var inputs = page.FindAll("input[type=password]");
        inputs.Count.Should().Be(3, "current, new and confirm");

        inputs[0].Change(CurrentPassword);
        inputs[1].Change(ChosenPassword);
        inputs[2].Change(ChosenPassword);
    }

    [Test]
    public void AfterASuccessfulChange_ThePageLeavesThroughTheRefreshEndpoint()
    {
        // The literal is deliberate: this pins the contract between the page and the endpoint, and a
        // test that followed a renamed constant automatically would not notice them drifting apart.
        var page = _ctx.Render<ChangePassword>();
        Fill(page);

        page.Find("form").Submit();

        var navigation = (BunitNavigationManager)_ctx.Services.GetRequiredService<NavigationManager>();

        navigation.History.Should().NotBeEmpty("the page must navigate away after a successful change");
        var target = navigation.History.Last();

        target.Uri.Should().Contain("refresh-signin",
            "clearing the flag on the user record is not enough - a Blazor circuit cannot write a " +
            "cookie, so the page must leave through the endpoint that can. Navigating straight to " +
            "\"/\" is the Pass 17 bug, and no HTTP test can see it. Went to: {0}", target.Uri);

        target.Options.ForceLoad.Should().BeTrue(
            "it has to be a real HTTP request, not an in-circuit navigation, or no cookie is written");
    }

    [Test]
    public async Task AfterASuccessfulChange_TheFlagIsActuallyCleared()
    {
        // The other half of the same handler, so the navigation test above cannot pass on a page
        // that navigates correctly without having done the work.
        var page = _ctx.Render<ChangePassword>();
        Fill(page);

        page.Find("form").Submit();

        using var scope = _ctx.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByNameAsync(UserName);

        user!.MustChangePassword.Should().BeFalse();
        (await users.CheckPasswordAsync(user, ChosenPassword)).Should().BeTrue(
            "the new password must actually be in force");
    }
}
