#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using CleanArchitecture.Blazor.Application.Common.Constants;
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

    // ---- supported-provider check ---------------------------------------------------------------

    [Test]
    public void AnUnsupportedProvider_FailsValidationNamingTheValueAndTheSupportedSet()
    {
        // Before this check a non-empty but unusable value passed validation and only blew up later,
        // in UseDatabase's default arm, as "DB Provider mysql is not supported."
        var options = BindOptions(new Dictionary<string, string?>
        {
            ["DatabaseSettings:DBProvider"] = "mysql",
            ["DatabaseSettings:ConnectionString"] = "Server=localhost;Database=app"
        });

        var act = () => options.Value;

        var failure = act.Should().Throw<OptionsValidationException>().Which.Failures.Single();
        failure.Should().Contain("'mysql' is not supported");
        failure.Should().Contain(DbProviderKeys.SqLite)
            .And.Contain(DbProviderKeys.SqlServer)
            .And.Contain(DbProviderKeys.Npgsql);
    }

    [TestCase(DbProviderKeys.SqLite)]
    [TestCase(DbProviderKeys.SqlServer)]
    [TestCase(DbProviderKeys.Npgsql)]
    public void EverySupportedProviderKey_Validates(string provider)
    {
        var options = BindOptions(new Dictionary<string, string?>
        {
            ["DatabaseSettings:DBProvider"] = provider,
            ["DatabaseSettings:ConnectionString"] = "Data Source=app.db"
        });

        options.Value.DBProvider.Should().Be(provider);
    }

    [Test]
    public void TheProviderCheckIsCaseInsensitive_MatchingUseDatabase()
    {
        // UseDatabase switches on DBProvider.ToLowerInvariant(), so "SQLite" is usable and must not
        // be rejected here.
        var options = BindOptions(new Dictionary<string, string?>
        {
            ["DatabaseSettings:DBProvider"] = "SQLite",
            ["DatabaseSettings:ConnectionString"] = "Data Source=app.db"
        });

        options.Value.DBProvider.Should().Be("SQLite");
    }

    [Test]
    public void TheSupportedSetIsExactlyTheThreeProvidersUseDatabaseCanDispatch()
    {
        // A hand-written list, deliberately. The validator now reads its set from the dispatch table
        // DependencyInjection.UseDatabase resolves an arm from, so a test that read that same table
        // would move with it and prove nothing - which is exactly what this test used to do against
        // DbProviderKeys. Pass 42 §4.1 settled it by mutation: a fourth key was added to
        // DbProviderKeys and all ten tests here stayed green, while "oracle" started passing
        // validation for a provider UseDatabase would have thrown on.
        //
        // Naming the three here makes this the second, independent source. Adding or removing a
        // provider arm reddens it, and that is the prompt to check the two provider switches
        // validation does NOT see - UseExceptionProcessor, and SerilogExtensions' sink selection.
        var settings = new DatabaseSettings { ConnectionString = "x", DBProvider = "definitely-not-a-provider" };

        var message = settings.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(settings))
            .Single().ErrorMessage!;

        const string preamble = "supported providers are: ";
        var listed = message[(message.IndexOf(preamble, StringComparison.Ordinal) + preamble.Length)..]
            .Split(", ");

        listed.Should().BeEquivalentTo(new[] { "postgresql", "mssql", "sqlite" },
            "the validator accepts exactly the providers UseDatabase has an arm for, and this list " +
            "is the only place that says which three those are without reading the same source");
    }
}
#nullable restore
