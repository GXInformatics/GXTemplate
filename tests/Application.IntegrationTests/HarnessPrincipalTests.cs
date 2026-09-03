#nullable enable
using System.Linq;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Domain.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.IntegrationTests;

using static Testing;

/// <summary>
/// The harness's own principal helpers, exercised.
/// </summary>
/// <remarks>
/// <c>RunAsAdministratorAsync</c> could never succeed. It resolved
/// <c>RoleManager&lt;IdentityRole&gt;</c> while the application registers
/// <c>.AddRoles&lt;ApplicationRole&gt;()</c>, so <c>GetService</c> returned <c>null</c> and the next
/// line threw <c>NullReferenceException</c>.
/// <para>
/// <b>It went unnoticed because nothing called it.</b> <c>RunAsDefaultUserAsync</c> passes an empty
/// roles array, so the <c>if (roles.Any())</c> block never ran and the null was never dereferenced -
/// a defect perfectly hidden behind the one code path anybody used. Pass 28 found it only by writing
/// a probe that wanted a role-bearing principal.
/// </para>
/// <para>
/// <b>These tests exist so it cannot rot back.</b> A helper no test calls is a helper that is not
/// known to work; the repair is only durable if something exercises it. That is the whole content of
/// this fixture - it asserts almost nothing about the application and everything about the harness.
/// </para>
/// </remarks>
[TestFixture]
public class HarnessPrincipalTests : TestBase
{
    [Test]
    public async Task RunAsAdministratorAsync_Succeeds()
    {
        // RED before Pass 29: NullReferenceException from inside the helper.
        var userId = await RunAsAdministratorAsync();

        userId.Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task RunAsAdministratorAsync_ActuallyCreatesTheRoleAndAssignsIt()
    {
        // Not merely "it did not throw". The helper's purpose is a role-bearing principal, so the
        // role has to exist and the user has to be in it - otherwise a green test here would still
        // leave §D unable to build the principal it needs.
        var userId = await RunAsAdministratorAsync();

        using var scope = CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();

        (await roles.RoleExistsAsync("Admin")).Should().BeTrue("the helper creates the role");

        var user = await users.FindByIdAsync(userId);
        user.Should().NotBeNull();
        (await users.GetRolesAsync(user!)).Should().Contain("Admin", "the helper assigns it");
    }

    [Test]
    public async Task AUserAskedForNoRoles_GetsNone()
    {
        // The path that DID work, asserted alongside the one that did not - so a future change to
        // the shared RunAsUserAsync body cannot fix one by breaking the other.
        //
        // A distinct name, not RunAsDefaultUserAsync: TestBase.TestSetUp calls ResetState, which
        // already establishes TestUser, and Identity rejects the duplicate.
        var userId = await RunAsUserAsync("NoRoleUser", "Password123!", new string[] { });

        using var scope = CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByIdAsync(userId);

        user.Should().NotBeNull();
        (await users.GetRolesAsync(user!)).Should().BeEmpty("no roles were asked for");
    }
}
#nullable restore
