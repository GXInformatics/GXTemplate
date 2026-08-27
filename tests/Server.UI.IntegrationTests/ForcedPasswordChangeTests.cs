#nullable enable
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Constants;
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
    public async Task OnceTheFlagIsCleared_TheApplicationIsReachable()
    {
        // The other end of the gate: it lets go.
        await _factory.ResetAdministratorPasswordAsync(mustChangePassword: false);

        using var client = _factory.CreateNonRedirectingClient();
        await CookieLogin.SignInAndExpectSuccessAsync(
            client, Users.Administrator, GxWebApplicationFactory.KnownPassword);

        var response = await client.SendAsync(Navigation("/"));

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "a user who no longer carries the flag reaches the application; redirected to {0}",
            response.Headers.Location?.ToString() ?? "(nowhere)");
    }
}
#nullable restore
