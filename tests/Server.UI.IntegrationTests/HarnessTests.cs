#nullable enable
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Infrastructure.Configurations;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Logging;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// What the harness itself promises: the host runs on PostgreSQL, on this assembly's own
/// databases, and no two tests in the assembly run at the same time (pass 47).
/// </summary>
[TestFixture]
public class HarnessTests
{
    private const string NpgsqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    [Test]
    public void TheWholeAssemblyRunsOneTestAtATime()
    {
        // Every host here shares the business database and resets it before booting, so two fixtures in parallel
        // would empty each other's installation. NUnit's default is serial, but nothing enforced it:
        // one [Parallelizable] fixture would have changed that silently.
        var assembly = typeof(HarnessTests).Assembly;

        // Not "?.Properties...Should()": a null-conditional skips the whole chain, assertion included,
        // so a missing attribute would have asserted nothing (GX ProjectTracking pass 4d, mutation E1).
        var level = assembly.GetCustomAttribute<LevelOfParallelismAttribute>();
        level.Should().NotBeNull("the assembly must cap NUnit at one worker");
        level!.Properties.Get("LevelOfParallelism").Should().Be(1, "one worker, so nothing runs beside anything else");

        var assemblyScope = assembly.GetCustomAttribute<ParallelizableAttribute>();
        assemblyScope.Should().BeOfType<NonParallelizableAttribute>(
            "the assembly states that it does not run in parallel");

        var parallelFixtures = assembly.GetTypes()
            .Where(t => t.GetCustomAttributes<ParallelizableAttribute>(inherit: true)
                .Any(a => a is not NonParallelizableAttribute && a.Scope != ParallelScope.None))
            .Select(t => t.Name)
            .ToList();
        parallelFixtures.Should().BeEmpty("no fixture may opt back into parallel execution");
    }

    [Test]
    public async Task TheHostRunsOnPostgreSql_BothContexts()
    {
        // No file-database fallback: a host that booted on anything else would still pass most of this
        // suite, and would be testing a provider the tests no longer run on (pass 47).
        using var factory = new GxWebApplicationFactory();
        using (var client = factory.CreateNonRedirectingClient())
        {
            await client.GetAsync("/");
        }

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IOptions<DatabaseSettings>>().Value.DBProvider
            .Should().Be(DbProviderKeys.Npgsql);

        await using var business = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContextAsync();
        business.Database.ProviderName.Should().Be(NpgsqlProviderName);

        await using var logs = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<LogDbContext>>().CreateDbContextAsync();
        logs.Database.ProviderName.Should().Be(NpgsqlProviderName);
    }

    [Test]
    public void TheHostIsGivenThisAssemblysOwnDatabases()
    {
        // The business database belongs to this assembly alone, so the other test assemblies, which dotnet test
        // may run at the same time, can never empty it.
        using var factory = new GxWebApplicationFactory();

        new NpgsqlConnectionStringBuilder(factory.BusinessConnectionString).Database
            .Should().Be(UiTestDatabase.BusinessDatabaseName);
        new NpgsqlConnectionStringBuilder(factory.LogConnectionString).Database
            .Should().Be(UiTestDatabase.LogDatabaseName);
    }
}
#nullable restore
