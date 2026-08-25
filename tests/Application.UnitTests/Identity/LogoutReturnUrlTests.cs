#nullable enable
using CleanArchitecture.Blazor.Server.UI.Services;
using FluentAssertions;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Identity;

/// <summary>
/// Where the sign-out endpoint sends the browser afterwards.
/// <para>
/// Upstream passed the posted value straight to <c>TypedResults.LocalRedirect</c>, which THROWS on
/// anything non-local - including the empty string. Since the sign-out has already completed by that
/// point, the user got a 500 while their session was genuinely gone. The rule is now a testable
/// static: local values are honoured, everything else falls back to the login page.
/// </para>
/// <para>
/// The second half matters independently of the crash: this endpoint runs for a user who is, by the
/// time the redirect is written, no longer authenticated. Following an attacker-supplied absolute
/// URL here would make it an open redirect.
/// </para>
/// </summary>
[TestFixture]
public class LogoutReturnUrlTests
{
    private const string Fallback = "/account/login";

    // ---- honoured -------------------------------------------------------------------------------

    [TestCase("/")]
    [TestCase("/account/login")]
    [TestCase("/pages/documents")]
    [TestCase("/system/tenants?tab=2")]
    [TestCase("~/account/login")]
    public void ALocalUrlIsHonoured(string candidate)
    {
        IdentityComponentsEndpointRouteBuilderExtensions
            .ResolveLocalReturnUrl(candidate, Fallback)
            .Should().Be(candidate);
    }

    // ---- the crash that was ---------------------------------------------------------------------

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void AMissingUrlFallsBackInsteadOfThrowing(string? candidate)
    {
        // This is the exact input that produced the 500: the form value did not bind, so
        // LocalRedirect was handed an empty string and rejected it as non-local.
        var act = () => IdentityComponentsEndpointRouteBuilderExtensions
            .ResolveLocalReturnUrl(candidate, Fallback);

        act.Should().NotThrow();
        act().Should().Be(Fallback);
    }

    // ---- not followed ---------------------------------------------------------------------------

    [TestCase("https://evil.example.com/steal")]
    [TestCase("http://evil.example.com")]
    [TestCase("//evil.example.com")]
    [TestCase("/\\evil.example.com")]
    [TestCase("javascript:alert(1)")]
    [TestCase("account/login")]
    [TestCase("~")]
    [TestCase("~\\evil")]
    public void ANonLocalUrlIsDiscardedRatherThanFollowed(string candidate)
    {
        IdentityComponentsEndpointRouteBuilderExtensions
            .ResolveLocalReturnUrl(candidate, Fallback)
            .Should().Be(Fallback, "following it would make sign-out an open redirect");
    }

    [Test]
    public void TheProtocolRelativeAndBackslashFormsAreBothRejected()
    {
        // Some browsers normalise "/\host" into "//host", so rejecting only one of the two leaves
        // the hole open. Pinned separately because it is the easy half to forget.
        IdentityComponentsEndpointRouteBuilderExtensions
            .ResolveLocalReturnUrl("//evil.example.com", Fallback).Should().Be(Fallback);
        IdentityComponentsEndpointRouteBuilderExtensions
            .ResolveLocalReturnUrl("/\\evil.example.com", Fallback).Should().Be(Fallback);
    }

    [Test]
    public void ASingleSlashIsStillLocal()
    {
        IdentityComponentsEndpointRouteBuilderExtensions
            .ResolveLocalReturnUrl("/", Fallback).Should().Be("/");
    }
}
#nullable restore
