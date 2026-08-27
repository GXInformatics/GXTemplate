#nullable enable
using System;
using System.Collections.Generic;
using CleanArchitecture.Blazor.Infrastructure.Configurations;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Configurations;

/// <summary>
/// DefaultTimeZone is a wizard parameter, so a typo in it reaches a generated project as
/// configuration rather than as code. Every provisioning path eventually hands it to
/// TimeZoneInfo.FindSystemTimeZoneById, which throws - so without validation the first symptom of
/// "Africa/Lagose" would be an exception on somebody's first registration, long after startup.
///
/// Same idiom as DatabaseSettings and StorageSettings: the settings class owns the rule, and
/// ValidateDataAnnotations().ValidateOnStart() turns it into a startup failure naming the value.
/// </summary>
[TestFixture]
public class AppConfigurationSettingsValidationTests
{
    private static IOptions<AppConfigurationSettings> BindOptions(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var services = new ServiceCollection();
        services.AddOptions<AppConfigurationSettings>()
            .Bind(configuration.GetSection(AppConfigurationSettings.Key))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        return services.BuildServiceProvider().GetRequiredService<IOptions<AppConfigurationSettings>>();
    }

    [Test]
    public void TheDefaults_AreValid_AndAreTheRatifiedGXOnes()
    {
        var settings = BindOptions(new Dictionary<string, string?>()).Value;

        settings.AppName.Should().Be("GX Application");
        settings.DefaultTimeZone.Should().Be("UTC");
        settings.AllowSelfRegistration.Should().BeTrue("upstream's behaviour is the default");
    }

    [TestCase("UTC")]
    [TestCase("Africa/Lagos")]
    [TestCase("Europe/London")]
    public void ATimeZoneThisSystemRecognises_IsAccepted(string id)
    {
        var settings = BindOptions(new Dictionary<string, string?>
        {
            ["AppConfigurationSettings:DefaultTimeZone"] = id
        }).Value;

        settings.DefaultTimeZone.Should().Be(id);
        // The value the wizard writes must be one the provisioning paths can actually resolve.
        TimeZoneInfo.TryFindSystemTimeZoneById(settings.DefaultTimeZone, out _).Should().BeTrue();
    }

    [Test]
    public void AnUnrecognisedTimeZone_FailsStartup_NamingTheValue()
    {
        var options = BindOptions(new Dictionary<string, string?>
        {
            ["AppConfigurationSettings:DefaultTimeZone"] = "Africa/Lagose"
        });

        var act = () => options.Value;

        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().Contain(f =>
                f.Contains("Africa/Lagose") && f.Contains("is not a time zone this system recognises"));
    }

    [Test]
    public void AnEmptyTimeZone_FailsStartup()
    {
        var options = BindOptions(new Dictionary<string, string?>
        {
            ["AppConfigurationSettings:DefaultTimeZone"] = ""
        });

        var act = () => options.Value;

        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().Contain(f => f.Contains("DefaultTimeZone is not configured"));
    }

    [Test]
    public void SelfRegistrationCanBeTurnedOff_ByConfigurationAlone()
    {
        // The point of the flag being configuration rather than conditional source: a generated
        // project can change its mind without regenerating from the template.
        var settings = BindOptions(new Dictionary<string, string?>
        {
            ["AppConfigurationSettings:AllowSelfRegistration"] = "false"
        }).Value;

        settings.AllowSelfRegistration.Should().BeFalse();
    }
}
#nullable restore
