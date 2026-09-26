using System.ComponentModel.DataAnnotations;
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
    ///     The connection string for the separate database Serilog writes log rows into, and the
    ///     SystemLogs page reads them back from. Expected to name a second database on the same
    ///     server as <see cref="ConnectionString" />, so that log volume stays out of the business
    ///     database's backups and can be retained under its own policy.
    /// </summary>
    /// <remarks>
    ///     It lives in this section, beside <see cref="ConnectionString" />, rather than in a section
    ///     of its own, and that is the whole mechanism for "both databases use the same provider":
    ///     there is nowhere here to write a second <see cref="DBProvider" />, so a provider mismatch
    ///     cannot be expressed. Same server, same provider - supporting anything else would mean a
    ///     second sink switch, a second EF provider registration, and a verification matrix of nine
    ///     combinations instead of three, to serve a case the requirement excludes.
    ///     <para>
    ///     <b><see cref="Validate" /> deliberately has no rule for this property.</b> An absent log
    ///     connection string is a supported state: the application starts, serves and audits
    ///     normally, the database sink is simply not configured, and the startup check complains
    ///     loudly in the console and file sinks. Requiring it here would make logging - which is
    ///     best-effort by nature - as fatal at startup as the business database, which it is not.
    ///     </para>
    /// </remarks>
    public string LogConnectionString { get; set; } = string.Empty;

    /// <summary>
    ///     The provider keys this application can actually build a DbContext for: the keys of the
    ///     dispatch table <c>DependencyInjection.UseDatabase</c> resolves an arm from, so a value
    ///     accepted here is by construction a value that dispatches there.
    /// </summary>
    /// <remarks>
    ///     It used to be read off <see cref="DbProviderKeys"/> by reflection, which is the set of
    ///     provider names that EXIST rather than the set that is wired. Pass 42 §6.1 showed the
    ///     difference is a real hole and not a stylistic one: a fourth constant on that type made
    ///     its key pass validation here and then throw in <c>UseDatabase</c>'s default arm - the
    ///     failure this check exists to pre-empt, permitted by the check itself.
    /// </remarks>
    private static readonly string[] SupportedProviders = DependencyInjection.SupportedDatabaseProviders.ToArray();

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

        // Names where the value lives, because appsettings.json deliberately carries none (the GX
        // configuration layout): the gitignored development file locally, web.config on a server.
        if (string.IsNullOrEmpty(ConnectionString))
            yield return new ValidationResult(
                $"{nameof(DatabaseSettings)}.{nameof(ConnectionString)} is not configured. Set it in " +
                "src/Server.UI/appsettings.Development.json for local development; the server's web.config in deployment.",
                new[] { nameof(ConnectionString) });
    }
}
