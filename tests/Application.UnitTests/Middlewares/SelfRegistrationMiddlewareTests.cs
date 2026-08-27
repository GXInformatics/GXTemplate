#nullable enable
using CleanArchitecture.Blazor.Server.UI.Middlewares;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Middlewares;

/// <summary>
/// AllowSelfRegistration is a runtime flag rather than conditional source removal, so a generated
/// project can turn registration on or off without regenerating. These pin what "off" has to mean.
///
/// The load-bearing case is the external-login one. There are TWO doors that create an account
/// without an existing one: the registration pages, and the external-login callback that provisions
/// a brand-new user for an identity it does not recognise. Closing only the first would leave the
/// flag saying something untrue.
/// </summary>
[TestFixture]
public class SelfRegistrationMiddlewareTests
{
    [TestCase("/account/register")]
    [TestCase("/account/registerconfirmation")]
    [TestCase("/account/linkexternallogin")]
    [TestCase("/pages/authentication/performlinkexternallogin")]
    public void WhenSelfRegistrationIsOff_EveryAccountCreatingPathIsBlocked(string path)
    {
        SelfRegistrationMiddleware.ShouldBlock(new PathString(path), allowSelfRegistration: false)
            .Should().BeTrue();
    }

    [TestCase("/account/register")]
    [TestCase("/account/registerconfirmation")]
    [TestCase("/account/linkexternallogin")]
    [TestCase("/pages/authentication/performlinkexternallogin")]
    public void WhenSelfRegistrationIsOn_NothingIsBlocked(string path)
    {
        SelfRegistrationMiddleware.ShouldBlock(new PathString(path), allowSelfRegistration: true)
            .Should().BeFalse();
    }

    [TestCase("/account/login")]
    [TestCase("/pages/authentication/login")]
    [TestCase("/pages/authentication/externallogin")]
    [TestCase("/")]
    [TestCase("/health")]
    [TestCase("/files/ProfilePictures/user/avatar.jpg")]
    public void SigningInIsNeverBlocked(string path)
    {
        // Turning self-registration off must not turn the application off. In particular
        // /pages/authentication/externallogin is the sign-in half of external login and serves
        // accounts that already exist - only the provisioning half is closed.
        SelfRegistrationMiddleware.ShouldBlock(new PathString(path), allowSelfRegistration: false)
            .Should().BeFalse();
    }

    [TestCase("/Account/Register")]
    [TestCase("/ACCOUNT/REGISTER")]
    [TestCase("/account/register/")]
    [TestCase("/account/register/anything")]
    public void TheBlockIsNotDodgeableByCasingOrTrailingSegments(string path)
    {
        SelfRegistrationMiddleware.ShouldBlock(new PathString(path), allowSelfRegistration: false)
            .Should().BeTrue();
    }

    [Test]
    public void APathThatMerelyStartsWithABlockedOne_IsNotBlocked()
    {
        // "/account/registered-users" is a different route, not a sub-path of "/account/register".
        SelfRegistrationMiddleware.ShouldBlock(new PathString("/account/registered-users"), allowSelfRegistration: false)
            .Should().BeFalse();
    }
}
#nullable restore
