#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using CleanArchitecture.Blazor.Infrastructure.Configurations;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Configurations;

/// <summary>
/// <see cref="DatabaseSettings"/> has always carried a Validate method, but nothing ever called it:
/// the settings were bound with a plain services.Configure, so a missing provider or connection string
/// surfaced much later as an obscure failure on first database use. Infrastructure now binds them
/// through an options builder with ValidateDataAnnotations().ValidateOnStart().
///
/// These tests pin the two properties that fix depends on: that ValidateDataAnnotations actually runs
/// the existing IValidatableObject.Validate (so the settings class stays the single definition of the
/// rules), and that the failure carries that method's own messages.
/// </summary>
[TestFixture]
public class DatabaseSettingsValidationTests
{
    private static IOptions<DatabaseSettings> BindOptions(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var services = new ServiceCollection();
        services.AddOptions<DatabaseSettings>()
            .Bind(configuration.GetSection("DatabaseSettings"))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        return services.BuildServiceProvider().GetRequiredService<IOptions<DatabaseSettings>>();
    }

    [Test]
    public void AMissingProvider_FailsValidationWithTheSettingsClassOwnMessage()
    {
        var options = BindOptions(new Dictionary<string, string?>
        {
            ["DatabaseSettings:ConnectionString"] = "Data Source=app.db"
        });

        var act = () => options.Value;

        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().Contain(f => f.Contains("DatabaseSettings.DBProvider is not configured"));
    }

    [Test]
    public void AMissingConnectionString_FailsValidationWithTheSettingsClassOwnMessage()
    {
        var options = BindOptions(new Dictionary<string, string?>
        {
            ["DatabaseSettings:DBProvider"] = "sqlite"
        });

        var act = () => options.Value;

        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().Contain(f => f.Contains("DatabaseSettings.ConnectionString is not configured"));
    }

    [Test]
    public void AFullyConfiguredSection_Validates()
    {
        var options = BindOptions(new Dictionary<string, string?>
        {
            ["DatabaseSettings:DBProvider"] = "sqlite",
            ["DatabaseSettings:ConnectionString"] = "Data Source=app.db"
        });

        options.Value.DBProvider.Should().Be("sqlite");
        options.Value.ConnectionString.Should().Be("Data Source=app.db");
    }

    [Test]
    public void ValidateIsStillTheSingleDefinitionOfTheRules()
    {
        // The wiring adds no rules of its own: it runs this method. Asserting it directly keeps the
        // pairing honest if someone later moves the checks into attributes on one side only.
        var settings = new DatabaseSettings();

        var results = settings.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(settings)).ToList();

        results.Should().HaveCount(2);
        results.Select(r => r.ErrorMessage).Should().BeEquivalentTo(
            "DatabaseSettings.DBProvider is not configured",
            "DatabaseSettings.ConnectionString is not configured");
    }
}
#nullable restore
