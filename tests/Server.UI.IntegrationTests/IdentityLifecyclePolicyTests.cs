#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Domain.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// The two account-lifecycle policies ratified in Pass 22, proved against the real login endpoint.
/// </summary>
/// <remarks>
/// <b>§A — an unconfirmed address may reset its password, and a completed reset confirms it.</b> A
/// reset link proves mailbox control exactly as a confirmation link does; refusing it left a user
/// with no route back except an administrator, and after Pass 21's enumeration fix they got silence
/// rather than an explanation, so they could not discover why.
/// <para>
/// <b>§B — self-registration produces an INACTIVE account.</b> Self-registration exists so people
/// can ask for access, not so they can grant themselves access. The template ships
/// <c>AllowSelfRegistration = true</c>, so the default posture has to be safe for a deployment that
/// leaves it on and never thinks about it again.
/// </para>
/// <para>
/// These drive <c>/pages/authentication/login</c> through <see cref="CookieLogin"/> rather than
/// asserting on component state, because that endpoint is where both gates actually live:
/// <c>IdentityComponentsEndpointRouteBuilderExtensions</c> refuses an inactive account outright, and
/// <c>SignInManager.PasswordSignInAsync</c> refuses an unconfirmed one because
/// <c>RequireConfirmedEmail = true</c>. A test that checked only the Blazor page would miss both.
/// </para>
/// </remarks>
[TestFixture]
public class IdentityLifecyclePolicyTests
{
    private const string Password = "Gx-Policy-Password-1!";

    private GxWebApplicationFactory _factory = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp() => _factory = new GxWebApplicationFactory(Environments.Production);

    [OneTimeTearDown]
    public void OneTimeTearDown() => _factory.Dispose();

    private async Task<ApplicationUser> CreateUserAsync(
        string userName, bool emailConfirmed, bool isActive)
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = new ApplicationUser
        {
            UserName = userName,
            Email = $"{userName}@example.com",
            EmailConfirmed = emailConfirmed,
            IsActive = isActive,
            TenantId = null,
            CreatedAt = DateTime.UtcNow
        };

        var created = await users.CreateAsync(user, Password);
        created.Succeeded.Should().BeTrue(
            "the fixture must be able to arrange its own users: "
            + string.Join("; ", created.Errors.Select(e => e.Description)));

        return user;
    }

    private async Task<ApplicationUser> ReloadAsync(string userName)
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return (await users.FindByNameAsync(userName))!;
    }

    /// <summary>
    /// Whether the login endpoint actually signed the caller in.
    /// </summary>
    /// <remarks>
    /// <b>The status code cannot answer this.</b> <c>HandleSignInResult</c> answers every outcome
    /// with a 302 - success redirects to "/", a refused sign-in to /account/invaliduser, a locked
    /// one to /account/lockout - so "did it redirect?" is true whatever happened. The destination is
    /// the only thing that distinguishes them, and a helper that checks the status alone reports
    /// every failed login as a success. (<see cref="CookieLogin.SignInAndExpectSuccessAsync"/> has
    /// exactly that weakness; see the Pass 22 report.)
    /// </remarks>
    private async Task<bool> SignedInAsync(string userName, string? password = null)
    {
        using var client = _factory.CreateNonRedirectingClient();
        var response = await CookieLogin.SignInAsync(client, userName, password ?? Password);

        if (response.StatusCode is not (HttpStatusCode.Redirect or HttpStatusCode.Found)) return false;

        var destination = response.Headers.Location?.ToString() ?? string.Empty;
        return !destination.Contains("invaliduser", StringComparison.OrdinalIgnoreCase)
               && !destination.Contains("lockout", StringComparison.OrdinalIgnoreCase)
               && !destination.Contains("login", StringComparison.OrdinalIgnoreCase);
    }

    // ---- §A ------------------------------------------------------------------------------------

    /// <summary>
    /// The platform facts that make §A both necessary and sufficient: an unconfirmed address is
    /// refused at the real endpoint, and confirming it is enough to let the same credentials in.
    /// </summary>
    /// <remarks>
    /// <b>This is NOT the red-before test for §A, and must not be read as one.</b> It sets
    /// <c>EmailConfirmed</c> itself rather than going through <c>ResetPassword.razor</c>, so it
    /// would stay green with that page's change reverted - it asserts that the GATE behaves, not
    /// that any page moves the flag. An earlier version of this test re-implemented
    /// <c>ResetPassword.razor</c>'s logic inline and then asserted the copy worked, which proved
    /// nothing about the page at all.
    /// <para>
    /// The page's behaviour is covered by
    /// <c>IdentityLifecycleComponentTests.ResetPassword_ConfirmsTheAddress_WhenTheResetSucceeds</c>,
    /// which renders the real component and is red before the change and green after. The two
    /// together are the end-to-end claim: that test proves the flag is set by a completed reset,
    /// this one proves setting it is what unlocks sign-in.
    /// </para>
    /// <para>
    /// It cannot be done in one test: the app renders at
    /// <c>InteractiveServerRenderMode(prerender: false)</c>, so <c>ResetPassword.razor</c> is not
    /// reachable over HTTP by this fixture at all.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ConfirmingTheAddress_IsWhatUnlocksSignIn_ForAnOtherwiseValidAccount()
    {
        var name = "unconfirmed-resetter";
        await CreateUserAsync(name, emailConfirmed: false, isActive: true);

        (await SignedInAsync(name)).Should().BeFalse(
            "RequireConfirmedEmail is true, so an unconfirmed address cannot sign in - this is the "
            + "stranding §A exists to end, and it is real");

        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByNameAsync(name))!;
            user.EmailConfirmed = true;
            (await users.UpdateAsync(user)).Succeeded.Should().BeTrue();
        }

        (await SignedInAsync(name)).Should().BeTrue(
            "confirming the address is SUFFICIENT - nothing else about the account changed, so a "
            + "completed reset that confirms the address lets the user straight in, which is the "
            + "RequireConfirmedEmail interaction §A.3 asks to be proved");
    }

    // ---- §B ------------------------------------------------------------------------------------

    [Test]
    public async Task AnInactiveAccount_IsRefused_EvenWithTheCorrectPassword()
    {
        var name = "inactive-applicant";
        await CreateUserAsync(name, emailConfirmed: true, isActive: false);

        (await SignedInAsync(name)).Should().BeFalse(
            "a self-registered account is created inactive and waits for an administrator - "
            + "holding the right password is not itself approval");
    }

    [Test]
    public async Task AnAdministratorCanActivate_AndThenTheSameCredentialsWork()
    {
        var name = "applicant-to-be-approved";
        await CreateUserAsync(name, emailConfirmed: true, isActive: false);

        (await SignedInAsync(name)).Should().BeFalse("still waiting");

        // Exactly what Users.razor's ActivateUserAsync does.
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByNameAsync(name))!;
            user.IsActive = true;
            user.LockoutEnd = null;
            (await users.UpdateAsync(user)).Succeeded.Should().BeTrue();
        }

        (await SignedInAsync(name)).Should().BeTrue(
            "activation is the approval, and nothing else about the account changed");
    }

    /// <summary>
    /// The bootstrap administrator is active, because it is the only way into a fresh installation.
    /// </summary>
    [Test]
    public async Task TheBootstrapAdministrator_IsActiveAndCanSignIn()
    {
        await _factory.ResetAdministratorPasswordAsync(mustChangePassword: false);

        var administrator = await ReloadAsync(Users.Administrator);
        administrator.IsActive.Should().BeTrue();
        administrator.EmailConfirmed.Should().BeTrue();

        (await SignedInAsync(Users.Administrator, GxWebApplicationFactory.KnownPassword))
            .Should().BeTrue("a fresh installation has to be enterable");
    }
}
#nullable restore
