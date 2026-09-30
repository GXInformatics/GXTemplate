using CleanArchitecture.Blazor.Infrastructure;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CleanArchitecture.Blazor.TestSupport;

/// <summary>
/// DbContext options that build <b>the application's own model</b>, for tests that migrate or
/// compare against the real migrations.
/// </summary>
/// <remarks>
/// <para>
/// A bare <c>new ApplicationDbContext(options)</c> builds a SMALLER model than the application's:
/// <c>IdentityDbContext.OnModelCreating</c> maps <c>AspNetUserPasskeys</c> only when
/// <c>IdentityOptions.Stores.SchemaVersion</c> is Version3, and it reads that from
/// <c>IOptions&lt;IdentityOptions&gt;</c> on the options' APPLICATION service provider. Without it,
/// <c>Migrate()</c> fails with <c>PendingModelChangesWarning</c> (measured in pass 4a) and
/// <c>HasPendingModelChanges()</c> reports drift that does not exist.
/// </para>
/// <para>
/// The Identity settings come from <see cref="DependencyInjection.ConfigureIdentityOptions"/>,
/// the application's own, never restated. <c>ModelMatchesMigrationsTests</c> uses the same
/// <see cref="Services"/>, so the model the migrations are checked against and the model the test
/// databases are migrated with are one definition.
/// </para>
/// <para>
/// <c>EnableServiceProviderCaching(false)</c> matters as much. EF's internal service provider,
/// and so its model cache, is keyed without the application service provider. The first context
/// built anywhere in a test assembly would otherwise decide the model every later one gets, and a
/// bare context built by another fixture would silently shrink it.
/// </para>
/// </remarks>
public static class ApplicationModel
{
    /// <summary>The migrations assembly, resolved by name as the application resolves it.</summary>
    public const string PostgreSqlMigrationsAssembly = "CleanArchitecture.Blazor.Migrators.PostgreSQL";

    /// <summary>An application service provider carrying the application's Identity options.</summary>
    public static IServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<IdentityOptions>(DependencyInjection.ConfigureIdentityOptions);
        return services.BuildServiceProvider();
    }

    /// <summary>Npgsql options that build the application's model against the real migrations.</summary>
    public static DbContextOptions<ApplicationDbContext> NpgsqlOptions(string connectionString) =>
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .EnableServiceProviderCaching(false)
            .UseApplicationServiceProvider(Services())
            .UseNpgsql(connectionString, o => o.MigrationsAssembly(PostgreSqlMigrationsAssembly))
            .Options;
}
