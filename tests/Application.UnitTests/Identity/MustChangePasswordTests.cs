#nullable enable
using System.Linq;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Services.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Identity;

/// <summary>
/// The two halves the forced-password-change flow rests on that are not the redirect itself: the
/// flag reaching the principal as a claim, and the change clearing it while invalidating the
/// account's other sessions.
/// </summary>
[TestFixture]
public class MustChangePasswordTests
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
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddClaimsPrincipalFactory<ApplicationUserClaimsPrincipalFactory>();

        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private async Task<ApplicationUser> CreateUserAsync(bool mustChangePassword)
    {
        using var scope = _provider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            UserName = "someone",
            Email = "someone@example.com",
            EmailConfirmed = true,
            IsActive = true,
            MustChangePassword = mustChangePassword
        };
        (await userManager.CreateAsync(user, "Password123!")).Succeeded.Should().BeTrue();
        return user;
    }

    [Test]
    public async Task AFlaggedUsersPrincipalCarriesTheClaim()
    {
        var user = await CreateUserAsync(mustChangePassword: true);

        using var scope = _provider.CreateScope();
        var factory = scope.ServiceProvider
            .GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>();
        var principal = await factory.CreateAsync(user);

        principal.HasClaim(c => c.Type == ApplicationClaimTypes.MustChangePassword)
            .Should().BeTrue("enforcement reads this claim, not the database");
    }

    [Test]
    public async Task AnUnflaggedUsersPrincipalDoesNotCarryTheClaim()
    {
        var user = await CreateUserAsync(mustChangePassword: false);

        using var scope = _provider.CreateScope();
        var factory = scope.ServiceProvider
            .GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>();
        var principal = await factory.CreateAsync(user);

        principal.HasClaim(c => c.Type == ApplicationClaimTypes.MustChangePassword)
            .Should().BeFalse();
    }

    [Test]
    public async Task ChangingThePasswordBumpsTheSecurityStamp()
    {
        // The flow relies on this to invalidate the account's other sessions. It is Identity's own
        // behaviour rather than ours, which is exactly why it is worth pinning: if a future Identity
        // version stopped doing it, a forced change would silently leave old sessions alive.
        var user = await CreateUserAsync(mustChangePassword: true);
        var stampBefore = user.SecurityStamp;

        using var scope = _provider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var loaded = await userManager.FindByNameAsync("someone");

        (await userManager.ChangePasswordAsync(loaded!, "Password123!", "NewPassword456!"))
            .Succeeded.Should().BeTrue();

        var after = await userManager.FindByNameAsync("someone");
        after!.SecurityStamp.Should().NotBe(stampBefore, "other sessions must not survive the change");
    }

    [Test]
    public async Task ClearingTheFlagRemovesTheClaimFromAFreshPrincipal()
    {
        var user = await CreateUserAsync(mustChangePassword: true);

        using var scope = _provider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var factory = scope.ServiceProvider
            .GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>();

        var loaded = await userManager.FindByNameAsync("someone");
        (await userManager.ChangePasswordAsync(loaded!, "Password123!", "NewPassword456!"))
            .Succeeded.Should().BeTrue();
        loaded!.MustChangePassword = false;
        (await userManager.UpdateAsync(loaded)).Succeeded.Should().BeTrue();

        var reloaded = await userManager.FindByNameAsync("someone");
        var principal = await factory.CreateAsync(reloaded!);

        principal.HasClaim(c => c.Type == ApplicationClaimTypes.MustChangePassword)
            .Should().BeFalse("which is why the page forces a reload after changing the password");
    }

    [Test]
    public async Task TheFlagIsPersisted()
    {
        await CreateUserAsync(mustChangePassword: true);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await db.Users.AsNoTracking().SingleAsync();

        stored.MustChangePassword.Should().BeTrue();
    }
}
#nullable restore
