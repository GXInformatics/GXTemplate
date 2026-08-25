#nullable enable
using System.Collections.Generic;
using System.Security.Claims;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Server.UI.Middlewares;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Middlewares;

/// <summary>
/// The HTTP half of the forced-password-change gate.
/// <para>
/// The whole risk of this middleware is its allow-list. Too narrow and the user is trapped: the
/// change page itself redirects to itself, or the circuit cannot start, or sign-out is blocked and
/// the flag becomes a lockout. Too wide and a flagged user simply walks around it. Both directions
/// are pinned here.
/// </para>
/// </summary>
[TestFixture]
public class ForcePasswordChangeMiddlewareTests
{
    private static HttpContext Context(
        string path,
        bool authenticated = true,
        bool flagged = true,
        string? secFetchMode = "navigate",
        string accept = "text/html,application/xhtml+xml")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Headers.Accept = accept;
        if (secFetchMode is not null) context.Request.Headers["Sec-Fetch-Mode"] = secFetchMode;

        if (authenticated)
        {
            var claims = new List<Claim> { new(ClaimTypes.Name, "someone") };
            if (flagged) claims.Add(new Claim(ApplicationClaimTypes.MustChangePassword, "true"));
            context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
        }
        else
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity());
        }

        return context;
    }

    // ---- it redirects when it should -----------------------------------------------------------

    [TestCase("/")]
    [TestCase("/pages/documents")]
    [TestCase("/system/tenants")]
    [TestCase("/user/profile")]
    [TestCase("/jobs")]
    public void AFlaggedUserIsRedirectedAwayFromEverythingElse(string path)
    {
        ForcePasswordChangeMiddleware.ShouldRedirect(Context(path)).Should().BeTrue();
    }

    // ---- it leaves everyone else alone ---------------------------------------------------------

    [Test]
    public void AnUnflaggedUserIsNeverRedirected()
    {
        ForcePasswordChangeMiddleware.ShouldRedirect(Context("/pages/documents", flagged: false))
            .Should().BeFalse();
    }

    [Test]
    public void AnAnonymousRequestIsNeverRedirected()
    {
        // Anonymous requests are the fallback authorization policy's business, not this middleware's.
        ForcePasswordChangeMiddleware.ShouldRedirect(Context("/pages/documents", authenticated: false))
            .Should().BeFalse();
    }

    // ---- the allow-list ------------------------------------------------------------------------

    [Test]
    public void TheChangePasswordPageDoesNotRedirectToItself()
    {
        ForcePasswordChangeMiddleware.ShouldRedirect(Context("/account/change-password"))
            .Should().BeFalse("that is the redirect loop");
    }

    [TestCase("/_blazor")]
    [TestCase("/_blazor/negotiate")]
    [TestCase("/_framework/blazor.web.js")]
    [TestCase("/_content/MudBlazor/MudBlazor.min.css")]
    public void TheCircuitAndItsAssetsAreNeverRedirected(string path)
    {
        ForcePasswordChangeMiddleware.ShouldRedirect(Context(path))
            .Should().BeFalse("the change-password page cannot render without them");
    }

    [TestCase("/pages/authentication/logout")]
    [TestCase("/account/logout")]
    public void SignOutIsNeverBlocked(string path)
    {
        ForcePasswordChangeMiddleware.ShouldRedirect(Context(path))
            .Should().BeFalse("a flag that cannot be escaped is a lockout, not a prompt");
    }

    [TestCase("/account/login")]
    [TestCase("/pages/authentication/login")]
    public void TheSignInSurfaceIsNeverBlocked(string path)
    {
        ForcePasswordChangeMiddleware.ShouldRedirect(Context(path)).Should().BeFalse();
    }

    [Test]
    public void TheAllowListMatchesOnSegments_NotPrefixes()
    {
        // "/account/logout-everything" must not be allowed just because it starts with an allowed
        // string. Prefix matching is how allow-lists leak.
        ForcePasswordChangeMiddleware.ShouldRedirect(Context("/account/logout-everything"))
            .Should().BeTrue();
    }

    // ---- only navigations ----------------------------------------------------------------------

    [Test]
    public void ASubresourceRequestIsNotRedirected()
    {
        // Redirecting a stylesheet or a background fetch produces a broken page rather than a
        // visible bounce, and can corrupt the very page we are trying to send the user to.
        ForcePasswordChangeMiddleware.ShouldRedirect(
                Context("/css/app.css", secFetchMode: "no-cors", accept: "text/css"))
            .Should().BeFalse();
    }

    [Test]
    public void SecFetchModeWinsOverTheAcceptHeader()
    {
        // A fetch() that happens to accept text/html is still not a navigation.
        ForcePasswordChangeMiddleware.ShouldRedirect(
                Context("/pages/documents", secFetchMode: "cors"))
            .Should().BeFalse();
    }

    [Test]
    public void WithoutSecFetchMode_TheAcceptHeaderDecides()
    {
        ForcePasswordChangeMiddleware.ShouldRedirect(
                Context("/pages/documents", secFetchMode: null, accept: "text/html"))
            .Should().BeTrue();

        ForcePasswordChangeMiddleware.ShouldRedirect(
                Context("/pages/documents", secFetchMode: null, accept: "application/json"))
            .Should().BeFalse();
    }
}
#nullable restore
