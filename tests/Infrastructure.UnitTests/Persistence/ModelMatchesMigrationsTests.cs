using System;
using CleanArchitecture.Blazor.Infrastructure;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CleanArchitecture.Blazor.Infrastructure.UnitTests.Persistence;

/// <summary>
/// The model and the migrations agree - for all three providers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Pass 32 §3 changed a unique index in
/// <c>PicklistSetConfiguration</c>. Had it not regenerated the migrations, the model and the schema
/// would have diverged silently: every test would have stayed green, because they all run against
/// <c>EnsureCreated</c> or an in-memory database built FROM THE MODEL, and only a real
/// <c>dotnet ef database update</c> against a real server would have shown it. The defect Pass 32
/// found had been predicted by a comment in the very file it lived in and shipped anyway - a comment
/// addressed to a future pass has no failure mode. This does.
/// </para>
/// <para>
/// <b>All three providers, not one.</b> They are regenerated together by the README's procedure, so
/// a guard covering one is a guard that lets the other two drift. The three snapshots are not
/// interchangeable either: each carries its own provider annotations and column types, so
/// "SQLite is fine" says nothing about whether the PostgreSQL snapshot was rewritten.
/// </para>
/// <para>
/// <b>No database is touched.</b> <c>HasPendingModelChanges</c> compares the context's model with
/// the snapshot compiled into the migrations assembly. Both are in-memory artifacts; the connection
/// string below is a parseable placeholder that is never opened. That is what makes this affordable:
/// the harness is pinned to LocalDB in one place and has been a documented irritation since Pass 8,
/// and a guard that needed three live servers would not be run.
/// </para>
/// <para>
/// <b>What a failure means.</b> Someone changed an entity or an <c>IEntityTypeConfiguration</c> and
/// did not regenerate. The fix is never to edit a migration by hand - it is the procedure the README
/// documents under "If you change the model", run for ALL THREE providers, which the message below
/// spells out in full.
/// </para>
/// </remarks>
public class ModelMatchesMigrationsTests
{
    /// <summary>
    /// A connection string that PARSES for each provider and is never opened. EF needs one to build
    /// the options; it does not need a server to compare two models.
    /// </summary>
    private const string SqliteConnection = "DataSource=:memory:";
    private const string SqlServerConnection = "Server=(local);Database=GxModelCheck;Trusted_Connection=True;";
    private const string PostgreSqlConnection = "Host=localhost;Database=GxModelCheck;Username=gx;Password=gx";

    // Resolved by name at runtime, exactly as Infrastructure's UseDatabase resolves them - so a
    // renamed migrations assembly fails here rather than at a customer's first `database update`.
    private const string SqliteMigrations = "CleanArchitecture.Blazor.Migrators.SqLite";
    private const string SqlServerMigrations = "CleanArchitecture.Blazor.Migrators.MSSQL";
    private const string PostgreSqlMigrations = "CleanArchitecture.Blazor.Migrators.PostgreSQL";

    /// <summary>
    /// The application service provider the DbContext options must carry, and <b>the finding that
    /// made this test possible at all</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>IdentityDbContext.OnModelCreating</c> maps <c>IdentityUserPasskey</c> only when
    /// <c>IdentityOptions.Stores.SchemaVersion</c> is Version3 or later - and it reads that from
    /// <c>IOptions&lt;IdentityOptions&gt;</c> on the options' APPLICATION service provider, not from
    /// anything the context itself declares. The first draft of this test omitted it, and all three
    /// providers reported pending changes: a single
    /// <c>DropTableOperation(AspNetUserPasskeys)</c>, because the snapshot had the table and the
    /// test's model did not.
    /// </para>
    /// <para>
    /// <b>The migrations were correct and the test was wrong</b>, which was established by running
    /// the README's own procedure - <c>dotnet ef migrations add</c> against the SQLite migrator
    /// produced an EMPTY migration - rather than by trusting either side. Had that check not been
    /// made, this pass would have "fixed" three correct migrations.
    /// </para>
    /// <para>
    /// The settings come from <c>DependencyInjection.ConfigureIdentityOptions</c>, the application's
    /// own, rather than being restated here. A copy would make this test green while the
    /// application's model and its migrations disagreed - the exact failure it exists to catch.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Why every options builder below calls <c>EnableServiceProviderCaching(false)</c>.
    /// </summary>
    /// <remarks>
    /// <b>EF's internal service provider - and therefore its MODEL CACHE - is shared between
    /// contexts whose application service providers differ.</b> The application service provider is
    /// not part of the internal-provider cache key, so the first <c>ApplicationDbContext</c> built
    /// anywhere in this test assembly decides which model every later one gets. Other fixtures here
    /// build one from a bare options builder, whose model has no <c>AspNetUserPasskeys</c>; when
    /// they ran first, these tests compared THAT model against the snapshot and reported drift that
    /// does not exist.
    /// <para>
    /// It reproduced only in a full-assembly run - the fixture passed on its own - which is the
    /// worst shape a false failure can take. Opting out of the cache gives each context here its own
    /// model, at a cost of well under a second across the four tests.
    /// </para>
    /// </remarks>
    private static IServiceProvider ApplicationServices()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<IdentityOptions>(DependencyInjection.ConfigureIdentityOptions);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void TheSqliteMigrationsMatchTheModel() =>
        AssertNoPendingChanges(
            "SQLite",
            "src/Migrators/Migrators.SqLite",
            "sqlite",
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .EnableServiceProviderCaching(false)
                .UseApplicationServiceProvider(ApplicationServices())
                .UseSqlite(SqliteConnection, o => o.MigrationsAssembly(SqliteMigrations))
                .Options);

    [Fact]
    public void TheSqlServerMigrationsMatchTheModel() =>
        AssertNoPendingChanges(
            "SQL Server",
            "src/Migrators/Migrators.MSSQL",
            "mssql",
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .EnableServiceProviderCaching(false)
                .UseApplicationServiceProvider(ApplicationServices())
                .UseSqlServer(SqlServerConnection, o => o.MigrationsAssembly(SqlServerMigrations))
                .Options);

    [Fact]
    public void ThePostgreSqlMigrationsMatchTheModel() =>
        AssertNoPendingChanges(
            "PostgreSQL",
            "src/Migrators/Migrators.PostgreSQL",
            "postgresql",
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .EnableServiceProviderCaching(false)
                .UseApplicationServiceProvider(ApplicationServices())
                .UseNpgsql(PostgreSqlConnection, o => o.MigrationsAssembly(PostgreSqlMigrations))
                .Options);

    /// <summary>
    /// The passkey table is in the model, so the setting above is doing something.
    /// </summary>
    /// <remarks>
    /// Without this, someone could "fix" a future failure by dropping
    /// <c>UseApplicationServiceProvider</c> - which would make all three tests pass by comparing a
    /// smaller model against itself, and would leave the real drift undetected. This asserts the
    /// model under test is the APPLICATION's model and not a reduced one.
    /// </remarks>
    [Fact]
    public void TheModelUnderTestIsTheApplicationsModel()
    {
        using var context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .EnableServiceProviderCaching(false)
                .UseApplicationServiceProvider(ApplicationServices())
                .UseSqlite(SqliteConnection, o => o.MigrationsAssembly(SqliteMigrations))
                .Options);

        Assert.Contains(
            context.Model.GetEntityTypes(),
            e => e.GetTableName() == "AspNetUserPasskeys");
    }

    /// <summary>
    /// The failure message is the point of this method being shared.
    /// </summary>
    /// <remarks>
    /// A bare "pending model changes" sends the reader hunting: it names neither which provider is
    /// stale nor what to do, and the answer - regenerate all three through a command with four
    /// required arguments - is not guessable. So the message names the provider, the project, the
    /// exact command with this provider's key already in it, and the two things that are NOT the
    /// fix (editing a migration by hand; regenerating only the provider that failed).
    /// </remarks>
    private static void AssertNoPendingChanges(
        string provider,
        string migratorProject,
        string providerKey,
        DbContextOptions<ApplicationDbContext> options)
    {
        using var context = new ApplicationDbContext(options);

        Assert.False(
            context.Database.HasPendingModelChanges(),
            $"""
             The {provider} migrations no longer match the model.

             Something in the entities or in an IEntityTypeConfiguration changed without the
             migrations being regenerated. Every other test will stay green - they build their
             schema from the MODEL - and only a real `dotnet ef database update` would have shown
             this, at deployment time.

             Regenerate, from the repository root:

               DatabaseSettings__DBProvider={providerKey} dotnet ef migrations add <Name> \
                 --project {migratorProject} --startup-project src/Server.UI \
                 --context ApplicationDbContext

             Then do the same for the OTHER TWO providers - all three are regenerated together, and
             the README's "If you change the model" section carries the connection-string overrides
             a non-configured provider needs.

             Do NOT hand-edit a migration or its snapshot to make this pass. The snapshot is what
             the next migration diffs against, so an edited one is wrong for every migration after
             this point, not just this one.
             """);
    }
}
