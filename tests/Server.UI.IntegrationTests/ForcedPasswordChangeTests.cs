#nullable enable
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Domain.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// The forced-password-change gate (Pass 7-3 §F.3), over HTTP against the real pipeline.
/// </summary>
/// <remarks>
/// The bootstrap prints a generated administrator password once and flags the account
/// MustChangePassword, so the gate is the first thing every new deployment meets. Its risk is the
/// exemption list: too narrow and the user is trapped in a redirect loop or cannot sign out, too
/// wide and the gate leaks. Both directions are asserted here.
/// <para>
/// It also only ever redirects NAVIGATIONS - redirecting a stylesheet or an image produces a broken
/// page rather than a visible bounce - so the asset case is asserted too.
/// </para>
/// </remarks>
[TestFixture]
public class ForcedPasswordChangeTests
{
    private const string ChangePasswordPath = "/account/change-password";

    private GxWebApplicationFactory _factory = null!;
    private HttpClient _flagged = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new GxWebApplicationFactory(Environments.Production);

        // The flag is SET ON explicitly: this is the state a brand-new deployment is in, and stating it
        // rather than relying on the bootstrap keeps this fixture independent of the ones before it.
        await _factory.ResetAdministratorPasswordAsync(mustChangePassword: true);

        _flagged = _factory.CreateNonRedirectingClient();
        await CookieLogin.SignInAndExpectSuccessAsync(
            _flagged, Users.Administrator, GxWebApplicationFactory.KnownPassword);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _flagged.Dispose();
        _factory.Dispose();
    }

    private static HttpRequestMessage Navigation(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        request.Headers.TryAddWithoutValidation("Accept", "text/html");
        return request;
    }

    [TestCase("/")]
    [TestCase("/pages/documents")]
    [TestCase("/identity/users")]
    [TestCase("/user/profile")]
    [TestCase("/system/logs")]
    public async Task AFlaggedUserNavigating_IsHeldOnTheChangePasswordPage(string path)
    {
        var response = await _flagged.SendAsync(Navigation(path));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location!.ToString().Should().Contain(ChangePasswordPath);
    }

    [Test]
    public async Task TheChangePasswordPageItself_IsNotRedirected()
    {
        // Redirecting the destination to itself is the redirect loop the exemption list exists for.
        var response = await _flagged.SendAsync(Navigation(ChangePasswordPath));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task LeavingIsNeverBlocked()
    {
        // Too narrow an exemption list turns the flag into a lockout: a user who cannot sign out and
        // cannot go anywhere else has no way back.
        //
        // Two things about this request are load-bearing, and getting either wrong makes the gate
        // look like it is blocking logout when it is not:
        //  - the VERB: a GET on this POST-only endpoint is a 405, and
        //    UseStatusCodePagesWithReExecute then re-runs the pipeline for /not-found, which is not
        //    exempt - so the flagged user is redirected, by a different route than the one here;
        //  - a NON-EMPTY form body: the endpoint binds [FromForm], and an empty
        //    application/x-www-form-urlencoded body is a 400, which re-executes the same way.
        // Its own client: this test ends the session, and the shared one is still needed by the
        // tests that run after it.
        using var client = _factory.CreateNonRedirectingClient();
        await CookieLogin.SignInAndExpectSuccessAsync(
            client, Users.Administrator, GxWebApplicationFactory.KnownPassword);

        var token = await CookieLogin.GetAntiforgeryTokenAsync(client, ChangePasswordPath);
        var request = new HttpRequestMessage(HttpMethod.Post, "/pages/authentication/logout")
        {
            Content = new FormUrlEncodedContent(new[]
            {
                new System.Collections.Generic.KeyValuePair<string, string>("returnUrl", "/account/login")
            })
        };
        request.Headers.TryAddWithoutValidation("RequestVerificationToken", token);
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        request.Headers.TryAddWithoutValidation("Accept", "text/html");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location!.ToString().Should().Be("/account/login",
            "signing out must always be possible, flag or no flag");
    }

    [Test]
    public async Task AFlaggedUserHittingAMissingPage_IsStillHeldOnTheChangePasswordPage()
    {
        // Recorded rather than asserted as a defect: UseStatusCodePagesWithReExecute re-runs the
        // pipeline for /not-found, which is a navigation and is not exempt, so a flagged user
        // hitting any missing page lands on the change-password page. That is the right destination
        // for someone who still has to change their password - but it is the reason a GET on a
        // POST-only endpoint looks like the gate blocking logout.
        var response = await _flagged.SendAsync(Navigation("/a-page-that-does-not-exist"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location!.ToString().Should().Contain(ChangePasswordPath);
    }

    [Test]
    public async Task AnAssetRequest_IsNotRedirected()
    {
        // Only navigations are redirected. Bouncing an image or a stylesheet produces a broken page
        // rather than a visible redirect, which is a worse failure than the one being prevented.
        var response = await CookieLogin.GetAsAssetAsync(_flagged, "/files/Documents/whatever.png");

        response.Headers.Location?.ToString().Should().NotContain(ChangePasswordPath);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the file does not exist; the point is that the request reached the endpoint at all");
    }

    [Test]
    public async Task TheCircuitAndFrameworkAssets_AreNotRedirected()
    {
        // The change-password page is itself interactive: blocking these means it cannot render,
        // which looks like a hang rather than a redirect.
        var response = await _flagged.PostAsync("/_blazor/negotiate", new StringContent(string.Empty));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task AUserWhoCompletesTheForcedChange_ReachesTheApplication()
    {
        // The flow's own regression test, and the one that would have failed for every pass between
        // 7-3 and 17.
        //
        // What it replaced asserted the half that worked: it cleared the flag BEFORE signing in, so
        // the cookie was issued without the claim and the propagation path - the only part that was
        // broken - was never exercised. This starts where a real user starts: signed in WITH the
        // flag, carrying the claim in a live cookie.
        //
        // The paths are written as literals rather than through the route constants on purpose. This
        // pins an HTTP contract that the change-password page depends on, and a test that moved
        // automatically with a rename would not notice the page and the endpoint drifting apart.
        using var factory = new GxWebApplicationFactory(Environments.Production);
        await factory.ResetAdministratorPasswordAsync(mustChangePassword: true);

        using var client = factory.CreateNonRedirectingClient();
        await CookieLogin.SignInAndExpectSuccessAsync(
            client, Users.Administrator, GxWebApplicationFactory.KnownPassword);

        // Held on the change-password page, as a flagged user must be.
        var held = await client.SendAsync(Navigation("/"));
        held.StatusCode.Should().Be(HttpStatusCode.Found);
        held.Headers.Location!.ToString().Should().Contain(ChangePasswordPath,
            "the fixture must actually start inside the gate, or the rest proves nothing");

        // Exactly what ChangePassword.razor's handler does, in order: change the password (which
        // bumps the security stamp) and clear the flag on the user record.
        const string chosenPassword = "Gx-User-Chosen-Password-1!";
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider
                .GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByNameAsync(Users.Administrator);

            var changed = await users.ChangePasswordAsync(
                user!, GxWebApplicationFactory.KnownPassword, chosenPassword);
            changed.Succeeded.Should().BeTrue();

            user!.MustChangePassword = false;
            await users.UpdateAsync(user);
        }

        // ...and then the navigation the page performs. Clearing the flag on the record is not
        // enough on its own: a Blazor circuit cannot write a cookie, so the principal still carries
        // the stale claim until a real HTTP request rebuilds the ticket.
        var refresh = await client.SendAsync(Navigation("/pages/authentication/refresh-signin?returnUrl=%2F"));

        refresh.StatusCode.Should().Be(HttpStatusCode.Found,
            "the refresh endpoint redirects onward after reissuing the cookie");
        refresh.Headers.Location!.ToString().Should().Be("/",
            "it must honour the local return URL the page asked for");

        try
        {
            // The assertion this whole pass exists for: the next request is IN the application.
            var landed = await client.SendAsync(Navigation("/"));

            landed.StatusCode.Should().Be(HttpStatusCode.OK,
                "a user who has chosen a new password must reach the application, not be sent back to " +
                "{0} - redirected to {1}",
                ChangePasswordPath, landed.Headers.Location?.ToString() ?? "(nowhere)");
        }
        finally
        {
            // This test genuinely changes the administrator's password, and against a SERVER
            // database every fixture in the run shares one. Leaving it changed makes every later
            // sign-in with KnownPassword fail - which is exactly what it did on PostgreSQL before
            // this restore existed, while passing on SQLite where each fixture gets its own file.
            await factory.ResetAdministratorPasswordAsync(mustChangePassword: false);
        }
    }

    [Test]
    public async Task TheRefreshEndpoint_DropsTheClaimWithoutASecondSignIn()
    {
        // The same guarantee stated as the claim itself rather than as a redirect, so a future
        // change that made "/" reachable for some other reason could not make the test above pass
        // while the claim was still being carried.
        using var factory = new GxWebApplicationFactory(Environments.Production);
        await factory.ResetAdministratorPasswordAsync(mustChangePassword: true);

        using var client = factory.CreateNonRedirectingClient();
        await CookieLogin.SignInAndExpectSuccessAsync(
            client, Users.Administrator, GxWebApplicationFactory.KnownPassword);

        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider
                .GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByNameAsync(Users.Administrator);
            user!.MustChangePassword = false;
            await users.UpdateAsync(user);
        }

        // Before the refresh the record says one thing and the cookie still says another.
        var beforeRefresh = await client.SendAsync(Navigation("/"));
        beforeRefresh.StatusCode.Should().Be(HttpStatusCode.Found,
            "the cookie still carries the claim until something reissues it - this is the staleness " +
            "the refresh endpoint exists to close, and asserting it keeps the endpoint honest");

        await client.SendAsync(Navigation("/pages/authentication/refresh-signin?returnUrl=%2F"));

        var afterRefresh = await client.SendAsync(Navigation("/"));
        afterRefresh.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task TheRefreshEndpoint_IsNotAWayPastTheGate()
    {
        // It is on the middleware's AlwaysAllowed list, so it is worth pinning that it cannot be
        // used to LEAVE the gate while the flag is still set. It rebuilds the principal from the
        // user record, so a still-flagged user simply gets the claim back.
        using var factory = new GxWebApplicationFactory(Environments.Production);
        await factory.ResetAdministratorPasswordAsync(mustChangePassword: true);

        using var client = factory.CreateNonRedirectingClient();
        await CookieLogin.SignInAndExpectSuccessAsync(
            client, Users.Administrator, GxWebApplicationFactory.KnownPassword);

        await client.SendAsync(Navigation("/pages/authentication/refresh-signin?returnUrl=%2F"));

        var stillHeld = await client.SendAsync(Navigation("/"));

        stillHeld.StatusCode.Should().Be(HttpStatusCode.Found);
        stillHeld.Headers.Location!.ToString().Should().Contain(ChangePasswordPath,
            "refreshing a principal must not clear a flag the database still holds");
    }

    [Test]
    public async Task AnUnflaggedUser_ReachesTheApplication()
    {
        // What the old OnceTheFlagIsCleared_TheApplicationIsReachable covered: the gate lets go for
        // a user who never carried the flag. Kept, because it is the only test of that direction,
        // but it is no longer the flow's regression test - it cannot fail the way the flow failed.
        using var factory = new GxWebApplicationFactory(Environments.Production);
        await factory.ResetAdministratorPasswordAsync(mustChangePassword: false);

        using var client = factory.CreateNonRedirectingClient();
        await CookieLogin.SignInAndExpectSuccessAsync(
            client, Users.Administrator, GxWebApplicationFactory.KnownPassword);

        var response = await client.SendAsync(Navigation("/"));

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "a user who no longer carries the flag reaches the application; redirected to {0}",
            response.Headers.Location?.ToString() ?? "(nowhere)");
    }
}
#nullable restore
