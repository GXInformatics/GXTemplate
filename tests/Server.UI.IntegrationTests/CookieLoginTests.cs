#nullable enable
using System.Net;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Domain.Identity;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// The gap this whole harness exists to close: a real cookie sign-in, driven over HTTP against the
/// real application, asserted permanently instead of measured by hand once a pass.
/// </summary>
[TestFixture]
public class CookieLoginTests
{
    private GxWebApplicationFactory _factory = null!;
    private ApplicationUser _administrator = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new GxWebApplicationFactory(Environments.Production);
        _administrator = await _factory.ResetAdministratorPasswordAsync(mustChangePassword: false);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown() => _factory.Dispose();

    [Test]
    public async Task TheBootstrapProvisionsAnAdministrator_EvenInProduction()
    {
        // Pass 7-3's finding: before it, Production came up with a correct, empty schema and no
        // account to sign in with. ResetAdministratorPasswordAsync throws if there is none, so
        // reaching OneTimeSetUp at all is the assertion; this states it explicitly.
        //
        // On a shared database "there is an administrator" no longer proves the bootstrap ran: an
        // earlier fixture's boot, or an earlier run, may have left one. The bootstrap stamps
        // CreatedAt when it provisions, and the factory resets the database before this host boots,
        // so an account stamped after the factory was created is one THIS boot provisioned (pass 47).
        _administrator.CreatedAt.Should().NotBeNull()
            .And.BeOnOrAfter(_factory.ConstructedAtUtc,
                "the administrator must have been provisioned by this boot, not left over from an earlier one");

        using var client = _factory.CreateNonRedirectingClient();

        var response = await CookieLogin.SignInAsync(client, Users.Administrator, GxWebApplicationFactory.KnownPassword);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
    }

    [Test]
    public async Task AnAuthenticatedClient_ReachesAPageThatAnonymousCannotSee()
    {
        using var client = _factory.CreateNonRedirectingClient();
        await CookieLogin.SignInAndExpectSuccessAsync(client, Users.Administrator, GxWebApplicationFactory.KnownPassword);

        var response = await client.GetAsync("/");

        // The cookie is real: the fallback policy lets this through, and anonymous does not get 200
        // for the same URL (AnonymousMatrixTests).
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "signed in, but was redirected to {0}", response.Headers.Location?.ToString() ?? "(nowhere)");
    }

    [Test]
    public async Task TheWrongPassword_DoesNotSignIn()
    {
        using var client = _factory.CreateNonRedirectingClient();

        await CookieLogin.SignInAsync(client, Users.Administrator, "not-the-password");
        var response = await client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Contain("/account/login");
    }

    [Test]
    public async Task APostWithNoAntiforgeryToken_IsRefused()
    {
        // Recorded deliberately: the token requirement is what made this flow look undrivable, and
        // it is also a real protection. If it ever stops applying, that is a finding.
        using var client = _factory.CreateNonRedirectingClient();

        var response = await client.PostAsync(CookieLogin.LoginEndpoint,
            new System.Net.Http.FormUrlEncodedContent(new[]
            {
                new System.Collections.Generic.KeyValuePair<string, string>("userName", Users.Administrator),
                new System.Collections.Generic.KeyValuePair<string, string>("password", GxWebApplicationFactory.KnownPassword)
            }));

        response.StatusCode.Should().NotBe(HttpStatusCode.Redirect);
        (await client.GetAsync("/")).StatusCode.Should().Be(HttpStatusCode.Redirect,
            "a login POST that skipped antiforgery must not have produced a session");
    }
}
#nullable restore
