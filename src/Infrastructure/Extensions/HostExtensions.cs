using Microsoft.Extensions.Hosting;

namespace CleanArchitecture.Blazor.Infrastructure.Extensions;
public static class HostExtensions
{
    public static async Task InitializeDatabaseAsync(this IHost host)
    {
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
