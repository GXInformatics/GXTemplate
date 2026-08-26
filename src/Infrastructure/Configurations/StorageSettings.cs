using System.ComponentModel.DataAnnotations;
using System.Reflection;
using CleanArchitecture.Blazor.Application.Common.Constants;

namespace CleanArchitecture.Blazor.Infrastructure.Configurations;

/// <summary>
///     Configuration wrapper for the file-storage section
/// </summary>
public class StorageSettings : IValidatableObject
{
    /// <summary>
    ///     Storage key constraint. The section is named "Storage", so the validation messages below
    ///     quote "Storage:Provider" rather than the class name - it is what an operator actually sets.
    /// </summary>
    public const string Key = "Storage";

    /// <summary>
    ///     Which storage provider to build. One of <see cref="StorageProviderKeys"/>.
    /// </summary>
    public string Provider { get; set; } = StorageProviderKeys.Disk;

    /// <summary>
    ///     Root directory for the disk provider. Relative paths resolve against the content root.
    /// </summary>
    public string RootPath { get; set; } = "Files";

    /// <summary>
    ///     Azure Storage connection string. Required when <see cref="Provider"/> is azureblob.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    ///     The single blob container every key is stored in. Required when
    ///     <see cref="Provider"/> is azureblob.
    /// </summary>
    public string ContainerName { get; set; } = string.Empty;

    /// <summary>
    ///     How long a browser may cache a file served by the streaming endpoint.
    /// </summary>
    public int CacheControlMaxAgeSeconds { get; set; } = 3600;

    /// <summary>
    ///     The provider keys this application can actually build an IFileStorage for, read straight
    ///     off <see cref="StorageProviderKeys"/> so the set cannot drift from the switch in
    ///     <c>DependencyInjection.AddFileStorage</c> that consumes it.
    /// </summary>
    private static readonly string[] SupportedProviders = typeof(StorageProviderKeys)
        .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
        .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToArray();

    /// <summary>
    ///     Validates the entered configuration
    /// </summary>
    /// <param name="validationContext">Describes the context in which a validation check is performed.</param>
    /// <returns>The result of the validation</returns>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(Provider))
        {
            yield return new ValidationResult(
                $"{Key}:{nameof(Provider)} is not configured; supported providers are: {string.Join(", ", SupportedProviders)}",
                new[] { nameof(Provider) });
            yield break;
        }

        // AddFileStorage switches on Provider.ToLowerInvariant(), so match on the same value:
        // anything else reaches its default arm and throws long after startup would have.
        var provider = Provider.Trim().ToLowerInvariant();
        if (!SupportedProviders.Contains(provider))
        {
            yield return new ValidationResult(
                $"{Key}:{nameof(Provider)} '{Provider}' is not supported; " +
                $"supported providers are: {string.Join(", ", SupportedProviders)}",
                new[] { nameof(Provider) });
            yield break;
        }

        if (provider == StorageProviderKeys.Disk && string.IsNullOrWhiteSpace(RootPath))
            yield return new ValidationResult(
                $"{Key}:{nameof(RootPath)} is not configured, and the {StorageProviderKeys.Disk} provider needs a root directory",
                new[] { nameof(RootPath) });

        // Checked here rather than on first upload: an azureblob deployment missing its credentials
        // should fail to start, not fail the first time a user uploads a file.
        if (provider == StorageProviderKeys.AzureBlob)
        {
            if (string.IsNullOrWhiteSpace(ConnectionString))
                yield return new ValidationResult(
                    $"{Key}:{nameof(ConnectionString)} is not configured, and it is required when " +
                    $"{Key}:{nameof(Provider)} is '{StorageProviderKeys.AzureBlob}'",
                    new[] { nameof(ConnectionString) });

            if (string.IsNullOrWhiteSpace(ContainerName))
                yield return new ValidationResult(
                    $"{Key}:{nameof(ContainerName)} is not configured, and it is required when " +
                    $"{Key}:{nameof(Provider)} is '{StorageProviderKeys.AzureBlob}'",
                    new[] { nameof(ContainerName) });
        }
    }
}
