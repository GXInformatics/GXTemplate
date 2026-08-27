#nullable enable
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// The anonymous matrix, which has been re-measured by hand in Pass 4B, Pass 4B-H, Pass 7C and
/// Pass 7C-2. It is a test now.
/// </summary>
/// <remarks>
/// What it protects is the deny-by-default fallback policy plus the small set of endpoints that
/// deliberately opt out of it: the login page, the health check, the framework assets and the
/// Blazor circuit. Every one of those exemptions is a hole that was opened on purpose, and the
/// failure mode for all of them is silent - an endpoint that stops requiring authentication still
/// returns 200, which is what a working page looks like.
/// </remarks>
[TestFixture]
public class AnonymousMatrixTests
{
    private GxWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _factory = new GxWebApplicationFactory(Environments.Production);
        _client = _factory.CreateNonRedirectingClient();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    /// <summary>Everything behind the fallback policy: an anonymous caller is sent to the login page.</summary>
    [TestCase("/")]
    [TestCase("/pages/documents")]
    [TestCase("/identity/users")]
    [TestCase("/identity/roles")]
    [TestCase("/system/logs")]
    [TestCase("/system/audittrails")]
    [TestCase("/system/tenants")]
    [TestCase("/system/picklistset")]
    [TestCase("/user/profile")]
    [TestCase("/account/change-password")]
    public async Task ProtectedPages_ChallengeAnonymousCallers(string path)
    {
        var response = await _client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location!.ToString().Should().Contain("/account/login");
    }

    /// <summary>The deliberate anonymous surface. Each of these is an explicit opt-out.</summary>
    [TestCase("/account/login")]
    [TestCase("/account/register")]
    [TestCase("/account/forgot-password")]
    [TestCase("/account/lockout")]
    [TestCase("/account/invaliduser")]
    [TestCase("/Error")]
    [TestCase("/not-found")]
    [TestCase("/health")]
    public async Task TheAnonymousSurface_IsReachable(string path)
    {
        var response = await _client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task TheBlazorCircuitEndpoint_IsReachableAnonymously()
    {
        // The login page is interactive, so an anonymous visitor has to be able to negotiate a
        // circuit. The convention in ConfigureServer exempts ONLY /_blazor - if that convention
        // ever widened to the whole component builder, every protected page would open with it,
        // and ProtectedPages_ChallengeAnonymousCallers above is what would catch that.
        var response = await _client.PostAsync("/_blazor/negotiate", new StringContent(string.Empty));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task AStoredFilePath_IsNeverServedAnonymously()
    {
        // The /Files static mount that used to serve these to anyone is gone (Pass 7C-2 §C).
        var response = await CookieLogin.GetAsAssetAsync(_client, "/files/ProfilePictures/whoever/avatar.jpg");

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location!.ToString().Should().Contain("/account/login");
    }

    [Test]
    public async Task TheOldStyleFilesPath_IsNeverServedAnonymously()
    {
        // Route matching is case-insensitive, so /Files/... reaches the same authenticated endpoint
        // rather than a static mount. Either way an anonymous caller gets no bytes.
        var response = await CookieLogin.GetAsAssetAsync(_client, "/Files/ProfilePictures/whoever/avatar.jpg");

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        (await response.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
    }

    [Test]
    public async Task TheHangfireDashboard_IsNotOpenToAnonymousCallers()
    {
        var response = await _client.GetAsync("/jobs");

        response.StatusCode.Should().NotBe(HttpStatusCode.OK);
    }
}
#nullable restore
