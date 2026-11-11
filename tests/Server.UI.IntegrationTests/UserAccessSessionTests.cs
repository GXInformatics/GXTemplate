#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Services.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// Pass 60 plan §5 rows 31 and 44: a user deactivated, or locked out by an administrator, while signed in is refused on
/// their next action and signed out; a lockout from failed sign-ins leaves their open session alone (decision 15).
/// </summary>
/// <remarks>
/// The hub is driven in-process, as <c>ServerHubTenantIsolationTests</c> drives its hub: the real
/// <see cref="UserContextHubFilter"/> over the real loader, cache and database. The cookie is driven over HTTP with the
/// real sign-in. What a browser does next - reconnect, fail, reload, land on the sign-in page - was checked in a browser
/// (pass report, browser check).
/// </remarks>
[TestFixture]
[NonParallelizable]
public class UserAccessSessionTests
{
    private GxWebApplicationFactory _factory = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp() => _factory = new GxWebApplicationFactory(Environments.Production);

    [OneTimeTearDown]
    public void OneTimeTearDown() => _factory.Dispose();

    // ---- harness -------------------------------------------------------------------------------------

    private async Task<ApplicationUser> CreateSignedInUserAsync(System.Net.Http.HttpClient client)
    {
        var name = "p60-" + Guid.NewGuid().ToString("N")[..10];
        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { UserName = name, Email = name + "@example.test", EmailConfirmed = true, IsActive = true };
            (await userManager.CreateAsync(user, GxWebApplicationFactory.KnownPassword)).Succeeded.Should().BeTrue();
        }

        await CookieLogin.SignInAndExpectSuccessAsync(client, name, GxWebApplicationFactory.KnownPassword);
        (await client.GetAsync("/")).StatusCode.Should().Be(HttpStatusCode.OK, "the user is signed in");
        return await ReloadAsync(name);
    }

    private async Task<ApplicationUser> ReloadAsync(string userName)
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByNameAsync(userName))!;
    }

    private async Task UpdateAsync(string userName, Action<ApplicationUser> change)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await userManager.FindByNameAsync(userName))!;
        change(user);
        (await userManager.UpdateAsync(user)).Succeeded.Should().BeTrue();
    }

    private sealed class Caller(ClaimsPrincipal user) : HubCallerContext
    {
        public bool Aborted { get; private set; }
        public override string ConnectionId => "c-" + Guid.NewGuid().ToString("N")[..6];
        public override string? UserIdentifier => user.FindFirstValue(ClaimTypes.NameIdentifier);
        public override ClaimsPrincipal? User => user;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() => Aborted = true;
    }

    private sealed class EmptyHub : Hub;

    /// <summary>One hub invocation by <paramref name="user"/>'s open connection; true when the hub method ran.</summary>
    private async Task<(bool Ran, Caller Caller, Exception? Refusal)> InvokeAsync(ApplicationUser user)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id), new Claim(ClaimTypes.Name, user.UserName!)], "Identity.Application"));
        var caller = new Caller(principal);
        caller.Items[HubUserContext.ItemsKey] = new UserContext(user.Id, user.UserName!);

        var filter = ActivatorUtilities.CreateInstance<UserContextHubFilter>(_factory.Services);
        var invocation = new HubInvocationContext(caller, _factory.Services, new EmptyHub(), typeof(EmptyHub).GetMethod(nameof(Hub.Dispose))!, []);

        var ran = false;
        try
        {
            await filter.InvokeMethodAsync(invocation, _ => { ran = true; return ValueTask.FromResult<object?>(null); });
            return (ran, caller, null);
        }
        catch (HubException refusal)
        {
            return (ran, caller, refusal);
        }
    }

    // ---- 31 ------------------------------------------------------------------------------------------

    [Test]
    public async Task T31_AUserDeactivatedWhileSignedIn_IsRefusedOnTheirNextAction_AndSignedOut()
    {
        using var client = _factory.CreateNonRedirectingClient();
        var user = await CreateSignedInUserAsync(client);
        (await InvokeAsync(user)).Ran.Should().BeTrue("before deactivation the connection works");

        // Deactivated by an administrator, through the Users page's own calls.
        await UpdateAsync(user.UserName!, u =>
        {
            u.IsActive = false;
            u.LockoutEnd = UserAccess.AdministrativeLockoutEnd;
        });

        var (ran, caller, refusal) = await InvokeAsync(user);
        ran.Should().BeFalse("the next action does not run");
        refusal.Should().NotBeNull();
        caller.Aborted.Should().BeTrue("the connection is closed");

        var response = await client.GetAsync("/");
        response.StatusCode.Should().Be(HttpStatusCode.Redirect, "the cookie is rejected on the next request");
        response.Headers.Location!.ToString().Should().Contain("/account/login");
        (await client.GetAsync("/")).Headers.Location!.ToString().Should().Contain("/account/login",
            "the session was signed out, not merely refused once: its server-side ticket is gone");
    }

    [Test]
    public async Task T31_AUserLockedOutByAnAdministrator_WithoutDeactivation_IsAlsoRefused()
    {
        using var client = _factory.CreateNonRedirectingClient();
        var user = await CreateSignedInUserAsync(client);
        var stamp = user.SecurityStamp;

        await UpdateAsync(user.UserName!, u => u.LockoutEnd = UserAccess.AdministrativeLockoutEnd);

        (await ReloadAsync(user.UserName!)).SecurityStamp.Should().NotBe(stamp, "losing access bumps the security stamp in the same save");
        var (ran, caller, _) = await InvokeAsync(user);
        ran.Should().BeFalse();
        caller.Aborted.Should().BeTrue();
        (await client.GetAsync("/")).Headers.Location!.ToString().Should().Contain("/account/login");
    }

    // ---- 44: decision 15 -----------------------------------------------------------------------------

    [Test]
    public async Task T44_ALockoutFromFailedSignIns_LeavesTheOpenSessionWorking_AndBlocksNewSignIns()
    {
        using var client = _factory.CreateNonRedirectingClient();
        var user = await CreateSignedInUserAsync(client);
        var stamp = user.SecurityStamp;

        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var loaded = (await userManager.FindByIdAsync(user.Id))!;
            while (!await userManager.IsLockedOutAsync(loaded)) await userManager.AccessFailedAsync(loaded);
        }

        var locked = await ReloadAsync(user.UserName!);
        locked.LockoutEnd.Should().NotBeNull().And.BeBefore(DateTimeOffset.UtcNow.AddDays(1), "Identity's automatic lockout is time-limited");
        locked.SecurityStamp.Should().Be(stamp, "an automatic lockout does not revoke access");

        (await InvokeAsync(locked)).Ran.Should().BeTrue("the open connection keeps working");
        (await client.GetAsync("/")).StatusCode.Should().Be(HttpStatusCode.OK, "the open cookie session keeps working");

        using var other = _factory.CreateNonRedirectingClient();
        await CookieLogin.SignInAsync(other, user.UserName!, GxWebApplicationFactory.KnownPassword);
        (await other.GetAsync("/")).Headers.Location!.ToString().Should().Contain("/account/login", "a new sign-in is refused while locked out");
    }

    [Test]
    public void T44_AnAutomaticLockoutThatIsNotFinite_FailsStartup()
    {
        CleanArchitecture.Blazor.Infrastructure.DependencyInjection.IsAutomaticLockoutBounded(TimeSpan.FromMinutes(5)).Should().BeTrue();
        CleanArchitecture.Blazor.Infrastructure.DependencyInjection.IsAutomaticLockoutBounded(TimeSpan.MaxValue).Should().BeFalse();
        CleanArchitecture.Blazor.Infrastructure.DependencyInjection.IsAutomaticLockoutBounded(TimeSpan.Zero).Should().BeFalse();

        // The application registers the check as a startup validation of IdentityOptions.
        var unbounded = new IdentityOptions();
        unbounded.Lockout.DefaultLockoutTimeSpan = TimeSpan.MaxValue;
        var validators = _factory.Services.GetServices<IValidateOptions<IdentityOptions>>().ToList();
        validators.Select(v => v.Validate(Options.DefaultName, unbounded)).Should()
            .Contain(r => r.Failed && r.FailureMessage!.Contains("DefaultLockoutTimeSpan"));
        validators.Select(v => v.Validate(Options.DefaultName, _factory.Services.GetRequiredService<IOptions<IdentityOptions>>().Value))
            .Should().OnlyContain(r => !r.Failed, "the template's own five-minute lockout is fine");
    }
}
