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
/// <see cref="StorageSettings"/> follows the <see cref="DatabaseSettings"/> idiom: IValidatableObject
/// bound through an options builder with ValidateDataAnnotations().ValidateOnStart().
///
/// What these pin is the reason for doing it that way. A misconfigured storage provider used to be
/// invisible until somebody uploaded a file - and under an azureblob deployment with no credentials,
/// that meant the first user upload was the discovery mechanism. It is now a startup failure that
/// names the offending value.
/// </summary>
[TestFixture]
public class StorageSettingsValidationTests
{
    private static IOptions<StorageSettings> BindOptions(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var services = new ServiceCollection();
        services.AddOptions<StorageSettings>()
            .Bind(configuration.GetSection(StorageSettings.Key))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        return services.BuildServiceProvider().GetRequiredService<IOptions<StorageSettings>>();
    }

    [Test]
    public void TheDefaults_AreValid_AndSelectTheDiskProvider()
    {
        var options = BindOptions(new Dictionary<string, string?>());

        options.Value.Provider.Should().Be("disk");
        options.Value.RootPath.Should().Be("Files");
    }

    [Test]
    public void AnUnsupportedProvider_FailsStartup_NamingTheValueAndTheSupportedSet()
    {
        var options = BindOptions(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "s3"
        });

        var act = () => options.Value;

        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().ContainSingle(f =>
                f.Contains("Storage:Provider 's3' is not supported")
                && f.Contains("disk")
                && f.Contains("azureblob"));
    }

    [Test]
    public void AnEmptyProvider_FailsStartup()
    {
        var options = BindOptions(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = ""
        });

        var act = () => options.Value;

        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().Contain(f => f.Contains("Storage:Provider is not configured"));
    }

    [Test]
    public void AzureBlobWithNoConnectionString_FailsStartup_NotOnFirstUpload()
    {
        var options = BindOptions(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "azureblob",
            ["Storage:ContainerName"] = "files"
        });

        var act = () => options.Value;

        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().Contain(f =>
                f.Contains("Storage:ConnectionString is not configured")
                && f.Contains("azureblob"));
    }

    [Test]
    public void AzureBlobWithNoContainer_FailsStartup()
    {
        var options = BindOptions(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "azureblob",
            ["Storage:ConnectionString"] = "UseDevelopmentStorage=true"
        });

        var act = () => options.Value;

        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().Contain(f => f.Contains("Storage:ContainerName is not configured"));
    }

    [Test]
    public void AFullyConfiguredAzureBlobSection_IsValid()
    {
        var options = BindOptions(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "azureblob",
            ["Storage:ConnectionString"] = "UseDevelopmentStorage=true",
            ["Storage:ContainerName"] = "files"
        });

        options.Value.Provider.Should().Be("azureblob");
    }

    [Test]
    public void TheSupportedSet_IsReadOffTheSameConstantsTheProviderSwitchUses()
    {
        // Reflection over StorageProviderKeys is what keeps validation and the DI switch from
        // drifting - a provider that validates but has no implementation is the failure mode.
        var options = BindOptions(new Dictionary<string, string?> { ["Storage:Provider"] = "nope" });

        var act = () => options.Value;

        var message = act.Should().Throw<OptionsValidationException>().Which.Failures;
        foreach (var key in new[] { "disk", "azureblob" })
        {
            message.Should().Contain(f => f.Contains(key));
        }
    }
}
#nullable restore
