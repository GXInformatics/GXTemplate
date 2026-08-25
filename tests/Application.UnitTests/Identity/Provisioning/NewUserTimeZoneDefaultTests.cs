#nullable enable
using System;
using System.Reflection;
using CleanArchitecture.Blazor.Server.UI.Pages.Identity.Login;
using CleanArchitecture.Blazor.Server.UI.Pages.Identity.Register;
using FluentAssertions;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Identity.Provisioning;

/// <summary>
/// User-provisioning forms used to default a new user's time zone to <c>TimeZoneInfo.Local.Id</c> -
/// the zone of whatever machine happens to be running the server. That is wrong for every user not
/// colocated with it, and it silently changes when the app is redeployed elsewhere. UTC is the correct
/// fixed default; the forms still let the person pick their own zone.
/// </summary>
[TestFixture]
public class NewUserTimeZoneDefaultTests
{
    /// <summary>
    /// Guards the tests below: if the build host ever happened to run in UTC, they would pass whether
    /// or not the defect were fixed.
    /// </summary>
    [Test]
    public void TheseTestsAreMeaningfulOnlyOffUtc()
    {
        if (TimeZoneInfo.Local.Id == TimeZoneInfo.Utc.Id)
        {
            Assert.Inconclusive(
                "The host is running in UTC, so a server-local default is indistinguishable from a UTC one.");
        }
    }

    [Test]
    public void SelfRegistration_DefaultsToUtc_NotTheServersZone()
    {
        var model = new Register.InputModel();

        model.TimeZoneId.Should().Be(TimeZoneInfo.Utc.Id);
        model.TimeZoneId.Should().NotBe(TimeZoneInfo.Local.Id);
    }

    /// <summary>
    /// The external-login linking form provisions users through the same path
    /// (IdentityComponentsEndpointRouteBuilderExtensions reads the timezoneId this model produces), so
    /// it shared the defect. Its InputModel is private, hence the reflection.
    /// </summary>
    [Test]
    public void ExternalLoginProvisioning_DefaultsToUtc_NotTheServersZone()
    {
        var modelType = typeof(LinkExternalLogin)
            .GetNestedType("InputModel", BindingFlags.NonPublic | BindingFlags.Public);
        modelType.Should().NotBeNull("LinkExternalLogin still provisions users through a nested InputModel");

        var model = Activator.CreateInstance(modelType!)!;
        var timeZoneId = (string?)modelType!.GetProperty("TimeZoneId")!.GetValue(model);

        timeZoneId.Should().Be(TimeZoneInfo.Utc.Id);
        timeZoneId.Should().NotBe(TimeZoneInfo.Local.Id);
    }

    [Test]
    public void TheDefaultIsAResolvableTimeZoneId()
    {
        // UserProfile.LocalTimeOffset feeds this id straight into TimeZoneInfo.FindSystemTimeZoneById.
        var action = () => TimeZoneInfo.FindSystemTimeZoneById(new Register.InputModel().TimeZoneId!);

        action.Should().NotThrow();
        TimeZoneInfo.FindSystemTimeZoneById(new Register.InputModel().TimeZoneId!)
            .BaseUtcOffset.Should().Be(TimeSpan.Zero);
    }
}
#nullable restore
