using Microsoft.Extensions.Hosting;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Logging;

namespace CleanArchitecture.Blazor.Infrastructure.Extensions;
public static class HostExtensions
{
    public static async Task InitializeDatabaseAsync(this IHost host)
    {
        // FIRST, before the business database is touched, and specifically before seeding.
        //
        // On a fresh deployment every line the seeding emits - provisioning roles, the default
        // organisation, the administrator - is logged before anything else happens. Create the log
        // table afterwards and all of it is written to a table that does not exist yet and is lost:
        // the very first run, the one an operator is most likely to want a record of, would be the
        // one run with no log rows in it.
        //
        // It cannot prevent the business database from being prepared: it never throws, and its
        // failures are reported through the console and file sinks.
        await host.PrepareLogDatabaseAsync().ConfigureAwait(false);

        using (var scope = host.Services.CreateScope())
        {
            var initializer = scope.ServiceProvider.GetRequiredService<ApplicationDbContextInitializer>();

            // Schema, then the things the application cannot run without, in every environment.
            // Skipping provisioning outside Development is what used to leave a production database
            // correctly migrated and completely unusable - no roles, no account to sign in with.
            await initializer.InitialiseAsync().ConfigureAwait(false);
            await initializer.ProvisionAsync().ConfigureAwait(false);

            var env = host.Services.GetRequiredService<IHostEnvironment>();
            if (env.IsDevelopment())
            {
                await initializer.SeedSampleDataAsync().ConfigureAwait(false);
            }
        }
    }

   
}
