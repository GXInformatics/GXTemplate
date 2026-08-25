using System.ComponentModel.DataAnnotations;
using System.Reflection;
using CleanArchitecture.Blazor.Application.Common.Constants;

namespace CleanArchitecture.Blazor.Infrastructure.Configurations;

/// <summary>
///     Configuration wrapper for the database section
/// </summary>
public class DatabaseSettings : IValidatableObject
{
    /// <summary>
    ///     Database key constraint
    /// </summary>
    public const string Key = nameof(DatabaseSettings);

    /// <summary>
    ///     Represents the database provider, which to connect to
    /// </summary>
    public string DBProvider { get; set; } = string.Empty;

    /// <summary>
    ///     The connection string being used to connect with the given database provider
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    ///     The provider keys this application can actually build a DbContext for, read straight off
    ///     <see cref="DbProviderKeys"/> so the set cannot drift from the switch in
    ///     <c>DependencyInjection.UseDatabase</c> that consumes it.
    /// </summary>
    private static readonly string[] SupportedProviders = typeof(DbProviderKeys)
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
        if (string.IsNullOrEmpty(DBProvider))
            yield return new ValidationResult(
                $"{nameof(DatabaseSettings)}.{nameof(DBProvider)} is not configured",
                new[] { nameof(DBProvider) });
        else if (!SupportedProviders.Contains(DBProvider.ToLowerInvariant()))
            // UseDatabase switches on DBProvider.ToLowerInvariant(), so match on the same value:
            // anything else reaches its default arm and throws long after startup would have.
            yield return new ValidationResult(
                $"{nameof(DatabaseSettings)}.{nameof(DBProvider)} '{DBProvider}' is not supported; " +
                $"supported providers are: {string.Join(", ", SupportedProviders)}",
                new[] { nameof(DBProvider) });

        if (string.IsNullOrEmpty(ConnectionString))
            yield return new ValidationResult(
                $"{nameof(DatabaseSettings)}.{nameof(ConnectionString)} is not configured",
                new[] { nameof(ConnectionString) });
    }
}
