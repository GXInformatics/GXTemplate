#nullable enable
using System.Linq;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Persistence;

/// <summary>
/// <c>Roles.Users</c> gates three navigation entries in MenuService (Chatbot, Analytics, Banking), but
/// the seeder only ever created Admin and Basic, so the role did not exist, nobody could hold it, and
/// those entries were reachable by administrators alone. The seeder now creates it.
/// </summary>
[TestFixture]
public class RoleSeedingTests
{
    private SqliteConnection _connection = null!;
    private ServiceProvider _provider = null!;

    [SetUp]
    public async Task SetUp()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseSqlite(_connection));
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
        services.AddScoped<ApplicationDbContextInitializer>();

        _provider = services.BuildServiceProvider();

        // InitialiseAsync runs migrations, which this provider-agnostic in-memory database does not
        // carry; EnsureCreated builds the same schema for the seeding path under test.
        using var scope = _provider.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private async Task SeedAsync()
    {
        using var scope = _provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ApplicationDbContextInitializer>().SeedAsync();
    }

    private async Task<string[]> SeededRoleNamesAsync()
    {
        using var scope = _provider.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        return await roleManager.Roles.Select(r => r.Name!).ToArrayAsync();
    }

    [Test]
    public async Task Seeding_CreatesTheUsersRoleTheMenuGatesRequire()
    {
        await SeedAsync();

        (await SeededRoleNamesAsync()).Should().Contain(Roles.Users);
    }

    [Test]
    public async Task Seeding_CreatesAdminBasicAndUsers_AndNothingElse()
    {
        await SeedAsync();

        (await SeededRoleNamesAsync()).Should().BeEquivalentTo(Roles.Admin, Roles.Basic, Roles.Users);
    }

    [Test]
    public async Task TheUsersRole_GetsTheSameGrantAsBasic()
    {
        await SeedAsync();

        using var scope = _provider.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();

        var basic = await roleManager.FindByNameAsync(Roles.Basic);
        var users = await roleManager.FindByNameAsync(Roles.Users);

        var basicClaims = (await roleManager.GetClaimsAsync(basic!))
            .Where(c => c.Type == ApplicationClaimTypes.Permission).Select(c => c.Value).ToArray();
        var usersClaims = (await roleManager.GetClaimsAsync(users!))
            .Where(c => c.Type == ApplicationClaimTypes.Permission).Select(c => c.Value).ToArray();

        basicClaims.Should().NotBeEmpty("Basic is seeded with the Permissions.Products grant");
        usersClaims.Should().BeEquivalentTo(basicClaims,
            "Basic is the nearest precedent for an ordinary-member role, and nothing else in the "
            + "template says what Users should hold");
    }

    [Test]
    public async Task SeedingTwice_IsIdempotent()
    {
        await SeedAsync();
        var afterFirst = await SeededRoleNamesAsync();

        await SeedAsync();

        (await SeededRoleNamesAsync()).Should().BeEquivalentTo(afterFirst);

        using var scope = _provider.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        var users = await roleManager.FindByNameAsync(Roles.Users);
        var claims = await roleManager.GetClaimsAsync(users!);
        claims.Select(c => c.Value).Should().OnlyHaveUniqueItems("a second seed must not duplicate claims");
    }
}
#nullable restore
