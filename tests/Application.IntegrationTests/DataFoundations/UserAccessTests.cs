using System;
using System.Security.Claims;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Services.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using static CleanArchitecture.Blazor.Application.IntegrationTests.DataFoundations.ModelHarness;

namespace CleanArchitecture.Blazor.Application.IntegrationTests.DataFoundations;

/// <summary>
/// Pass 60 plan §5 row 32: the save that revokes a user's access bumps the security stamp and clears the cached user
/// context; reactivating restores it at once; and background work (an inactive, locked-out system account) still
/// authorizes, because <c>IdentityService.AuthorizeAsync</c> was deliberately left alone.
/// </summary>
[NonParallelizable]
public class UserAccessTests
{
    [OneTimeSetUp]
    public async Task Ready() => await RootAsync();

    private static ClaimsPrincipal PrincipalOf(string userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "Identity.Application"));

    [Test]
    public async Task T32_DeactivationBumpsTheStampAndClearsTheContext_AndReactivationRestoresIt()
    {
        var services = await RootAsync();
        var userId = await CreateUserAsync([]);
        var loader = services.GetRequiredService<IUserContextLoader>();

        (await loader.LoadAsync(PrincipalOf(userId))).Should().NotBeNull("an active user has a context, now cached");

        string stampBefore;
        using (var scope = services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await userManager.FindByIdAsync(userId))!;
            stampBefore = user.SecurityStamp!;
            user.IsActive = false;
            user.LockoutEnd = UserAccess.AdministrativeLockoutEnd;
            (await userManager.UpdateAsync(user)).Succeeded.Should().BeTrue();
        }

        using (var scope = services.CreateScope())
        {
            var user = (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(userId))!;
            user.SecurityStamp.Should().NotBe(stampBefore, "every cookie and circuit holding the old stamp now fails revalidation");
        }
        (await loader.LoadAsync(PrincipalOf(userId))).Should().BeNull("the cached context was invalidated by the save, not left for an hour");

        using (var scope = services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await userManager.FindByIdAsync(userId))!;
            user.IsActive = true;
            user.LockoutEnd = null;
            (await userManager.UpdateAsync(user)).Succeeded.Should().BeTrue();
        }
        (await loader.LoadAsync(PrincipalOf(userId))).Should().NotBeNull("reactivation is seen at once, not after the short negative cache");
    }

    [Test]
    public async Task T32_AnInactiveLockedOutAccount_StillAuthorizes_SoBackgroundWorkRuns()
    {
        // The system account is inactive and permanently locked out by design, and authorizes background work through
        // IdentityService.AuthorizeAsync. That path ignores IsActive and lockout, and must keep doing so.
        var services = await RootAsync();
        var userId = await CreateUserAsync([], Permissions.PicklistSets.View);
        using (var scope = services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await userManager.FindByIdAsync(userId))!;
            user.IsActive = false;
            user.LockoutEnd = UserAccess.AdministrativeLockoutEnd;
            await userManager.UpdateAsync(user);
        }

        using (var scope = services.CreateScope())
        {
            var identity = scope.ServiceProvider.GetRequiredService<IIdentityService>();
            (await identity.AuthorizeAsync(userId, Permissions.PicklistSets.View)).Should().BeTrue();
        }
    }
}
