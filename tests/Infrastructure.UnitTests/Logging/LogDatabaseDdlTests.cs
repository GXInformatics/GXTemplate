using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Logging;
using Microsoft.Data.SqlClient;
using Npgsql;
using Xunit;

namespace CleanArchitecture.Blazor.Infrastructure.UnitTests.Logging;

/// <summary>
/// The log DATABASE's provider SQL: what it asks, what it issues, what it derives, and what it
/// refuses. None of it needs a server.
/// </summary>
/// <remarks>
/// Pass 15 measured every error code and dialect fact these tests encode - that PostgreSQL has no
/// <c>CREATE DATABASE IF NOT EXISTS</c> (<c>42601</c>), that T-SQL has none either (<c>156</c>),
/// that a <c>NOCREATEDB</c> role still reads <c>pg_database</c>, and that denial is <c>42501</c> /
/// <c>262</c> while a lost race is <c>42P04</c> / <c>1801</c>. These pin the code that acts on them.
/// </remarks>
public class LogDatabaseDdlTests
{
    public static TheoryData<string> ServerProviders =>
        new() { DbProviderKeys.SqlServer, DbProviderKeys.Npgsql };

    // ------------------------------------------------------------- the guard contract

    [Theory]
    [MemberData(nameof(ServerProviders))]
    public void TheCreateStatementCarriesNoIfNotExists_BecauseNeitherServerHasOne(string provider)
    {
        // The reason these statements live here rather than in LogTableDdl. That class's contract -
        // asserted by LogTableDdlTests.EveryStatementIsGuarded - is that EVERY statement carries
        // IF NOT EXISTS, and no CREATE DATABASE can: PostgreSQL answers 42601 and T-SQL answers 156.
        // Sharing the member would have forced that assertion to be weakened for the table too.
        var statement = LogDatabaseDdl.CreateStatement(provider, "gx_logs");

        Assert.DoesNotContain("IF NOT EXISTS", statement, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CREATE DATABASE", statement, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SqlServerStillGuardsItsOwnWay_BecauseTSqlOffersDbId()
    {
        // T-SQL has no IF NOT EXISTS but it does have IF DB_ID(...) IS NULL - the same idiom
        // LogTableDdl uses for sys.tables. It costs nothing and makes the statement idempotent on
        // its own, independently of the catalogue pre-check.
        var statement = LogDatabaseDdl.CreateStatement(DbProviderKeys.SqlServer, "GxLogs");

        Assert.Contains("IF DB_ID(", statement, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IS NULL", statement, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PostgresGetsABareCreate_BecauseNoGuardedFormExists()
    {
        // Stated as an assertion so nobody "fixes" this by adding a guard that does not parse.
        // Idempotence on PostgreSQL comes from the pre-check, and a lost race from 42P04.
        var statement = LogDatabaseDdl.CreateStatement(DbProviderKeys.Npgsql, "gx_logs");

        Assert.Equal("CREATE DATABASE \"gx_logs\"", statement);
    }

    // ------------------------------------------------------------- the existence check

    [Theory]
    [MemberData(nameof(ServerProviders))]
    public void TheExistenceCheckIsParameterised_NotInterpolated(string provider)
    {
        // The half of this class that CAN be parameterised is. The database name arrives from
        // configuration, which nothing sanitises.
        var sql = LogDatabaseDdl.ExistsCommandText(provider);

        Assert.Contains(LogDatabaseDdl.NameParameter, sql, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExistenceChecksReadTheCatalogue_WhichNeedsNoPrivilege()
    {
        // Pass 15 proved both run under a login that cannot create databases at all - a NOCREATEDB
        // PostgreSQL role and a SQL Server login without dbcreator. That is the property that lets a
        // correctly provisioned production deployment find the database present and issue nothing,
        // so the create path never executes and the elevated grant never comes up.
        Assert.Contains("pg_database", LogDatabaseDdl.ExistsCommandText(DbProviderKeys.Npgsql));
        Assert.Contains("DB_ID(", LogDatabaseDdl.ExistsCommandText(DbProviderKeys.SqlServer));
    }

    // ------------------------------------------------------------- maintenance connection

    [Fact]
    public void ThePostgresMaintenanceConnection_SwapsOnlyTheDatabase()
    {
        const string configured =
            "Host=db.example.com;Port=6432;Database=gx_logs;Username=gx;Password=secret;Timeout=17";

        using var connection = LogDatabaseDdl.CreateMaintenanceConnection(DbProviderKeys.Npgsql, configured);
        var b = new NpgsqlConnectionStringBuilder(connection.ConnectionString);

        Assert.Equal("postgres", b.Database);

        // Everything else survives. Built through the provider's own builder rather than by string
        // surgery precisely so that TLS settings, timeouts and ports are not quietly dropped.
        Assert.Equal("db.example.com", b.Host);
        Assert.Equal(6432, b.Port);
        Assert.Equal("gx", b.Username);
        Assert.Equal(17, b.Timeout);
    }

    [Fact]
    public void TheSqlServerMaintenanceConnection_SwapsOnlyTheCatalogue()
    {
        const string configured =
            @"Server=db.example.com;Database=GxLogs;User Id=gx;Password=secret;Encrypt=True;Connect Timeout=17";

        using var connection = LogDatabaseDdl.CreateMaintenanceConnection(DbProviderKeys.SqlServer, configured);
        var b = new SqlConnectionStringBuilder(connection.ConnectionString);

        Assert.Equal("master", b.InitialCatalog);
        Assert.Equal("db.example.com", b.DataSource);
        Assert.Equal("gx", b.UserID);
        Assert.True(b.Encrypt);
        Assert.Equal(17, b.ConnectTimeout);
    }

    [Fact]
    public void TheDatabaseNameComesFromTheConfiguredConnectionString()
    {
        Assert.Equal("gx_logs", LogDatabaseDdl.DatabaseName(
            DbProviderKeys.Npgsql, "Host=h;Database=gx_logs;Username=u"));
        Assert.Equal("GxLogs", LogDatabaseDdl.DatabaseName(
            DbProviderKeys.SqlServer, @"Server=h;Database=GxLogs;Trusted_Connection=True"));
    }

    // ------------------------------------------------------------- identifier quoting

    [Theory]
    [InlineData(DbProviderKeys.Npgsql, "gx_logs", "\"gx_logs\"")]
    [InlineData(DbProviderKeys.SqlServer, "GxLogs", "[GxLogs]")]
    public void AnOrdinaryNameIsQuotedInTheProvidersOwnForm(string provider, string name, string expected) =>
        Assert.Equal(expected, LogDatabaseDdl.QuoteIdentifier(provider, name));

    [Fact]
    public void APostgresNameContainingADoubleQuote_IsEscapedRatherThanRejected()
    {
        // Doubling is the escape PostgreSQL defines and it is complete: the name becomes harmless
        // instead of breaking out of the identifier. The wizard's DatabaseName symbol is sanitised
        // by template.json, but appsettings.json and DatabaseSettings__LogConnectionString are not,
        // and this is the layer that reads them.
        Assert.Equal("\"ev\"\"il\"", LogDatabaseDdl.QuoteIdentifier(DbProviderKeys.Npgsql, "ev\"il"));
    }

    [Fact]
    public void ASqlServerNameContainingABracket_IsEscapedRatherThanRejected() =>
        Assert.Equal("[ev]]il]", LogDatabaseDdl.QuoteIdentifier(DbProviderKeys.SqlServer, "ev]il"));

    [Fact]
    public void AnInjectionAttemptCannotEscapeTheIdentifier()
    {
        // The shape that motivates the escaping: a name chosen to terminate the identifier and
        // append a statement. After quoting it is one identifier, oddly named and inert.
        var quoted = LogDatabaseDdl.QuoteIdentifier(DbProviderKeys.Npgsql, "x\"; DROP DATABASE \"gx");
        var statement = LogDatabaseDdl.CreateStatement(DbProviderKeys.Npgsql, "x\"; DROP DATABASE \"gx");

        Assert.Equal("\"x\"\"; DROP DATABASE \"\"gx\"", quoted);
        Assert.Equal(1, CountOccurrences(statement, "CREATE DATABASE"));
        Assert.Equal(0, CountOccurrences(statement, "DROP DATABASE \"gx\""));
    }

    [Fact]
    public void TheSqlServerLiteralGuardIsEscapedToo()
    {
        // DB_ID takes the name as a VALUE, where the escape is the single quote rather than the
        // bracket - so the guarded statement has two different escapings of the same name in it.
        var statement = LogDatabaseDdl.CreateStatement(DbProviderKeys.SqlServer, "ev'il]x");

        Assert.Contains("N'ev''il]x'", statement, StringComparison.Ordinal);
        Assert.Contains("[ev'il]]x]", statement, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyNameIsRefused(string name) =>
        Assert.Throws<InvalidOperationException>(
            () => LogDatabaseDdl.QuoteIdentifier(DbProviderKeys.Npgsql, name));

    [Theory]
    [InlineData("gx\0logs")]
    [InlineData("gx\nlogs")]
    public void ANameCarryingAControlCharacterIsRefused(string name)
    {
        // Defence in depth rather than necessity - doubling would render these safe too. No
        // legitimate database name contains a NUL or a newline, so a name that does is a
        // misconfiguration worth failing loudly on rather than quietly creating a database for.
        // The throw is caught by the startup check's non-fatal try, like every other failure there.
        Assert.Throws<InvalidOperationException>(
            () => LogDatabaseDdl.QuoteIdentifier(DbProviderKeys.Npgsql, name));
    }

    // ------------------------------------------------------------- outcome classification

    /// <remarks>
    /// PostgreSQL only, and deliberately. <see cref="PostgresException"/> has a public constructor
    /// taking the fields the wire protocol carries, so the object under test here is the real type
    /// with a real SqlState. <see cref="SqlException"/> has no public constructor at all, and a test
    /// that reflected into the driver's private factory to fabricate one would be asserting against
    /// an implementation detail of a NuGet package - it broke on the first attempt, on this very
    /// version. The SQL Server numbers are pinned instead by
    /// <c>LogDatabaseCreationAcceptanceTests</c>, which provokes genuine 262 and 1801 errors from a
    /// real server. That is better evidence than a fabricated exception, not worse.
    /// </remarks>
    [Fact]
    public void ARaceIsRecognisedAsAlreadyExisting_OnPostgres()
    {
        // 42P04 arriving after the pre-check said "absent" means another instance won the race. The
        // caller treats it as success.
        Assert.True(LogDatabaseDdl.IsAlreadyExists(PostgresError("42P04")));

        Assert.False(LogDatabaseDdl.IsAlreadyExists(PostgresError("42501")));
        Assert.False(LogDatabaseDdl.IsAlreadyExists(new InvalidOperationException()));
    }

    [Fact]
    public void ADenialIsRecognisedAsADenial_OnPostgres()
    {
        Assert.True(LogDatabaseDdl.IsPermissionDenied(PostgresError("42501")));

        Assert.False(LogDatabaseDdl.IsPermissionDenied(PostgresError("42P04")));
        Assert.False(LogDatabaseDdl.IsPermissionDenied(new InvalidOperationException()));
    }

    [Fact]
    public void TheTwoClassificationsDoNotOverlap()
    {
        // They lead to opposite outcomes - silence versus a loud Error - so an exception must never
        // satisfy both.
        foreach (var ex in new Exception[] { PostgresError("42P04"), PostgresError("42501") })
        {
            Assert.False(LogDatabaseDdl.IsAlreadyExists(ex) && LogDatabaseDdl.IsPermissionDenied(ex));
        }
    }

    [Fact]
    public void AnUnrelatedFailureIsNeitherRaceNorDenial()
    {
        // The default matters: anything unrecognised falls through to the caller's outer catch and
        // the ordinary diagnostic, rather than being quietly absorbed as a race would be.
        var unrelated = PostgresError("53300");   // too_many_connections

        Assert.False(LogDatabaseDdl.IsAlreadyExists(unrelated));
        Assert.False(LogDatabaseDdl.IsPermissionDenied(unrelated));
    }

    [Fact]
    public void TheRequiredGrantIsNamedPerProvider()
    {
        Assert.Contains("CREATEDB", LogDatabaseDdl.RequiredGrant(DbProviderKeys.Npgsql));
        Assert.Contains("dbcreator", LogDatabaseDdl.RequiredGrant(DbProviderKeys.SqlServer));
    }

    // ------------------------------------------------------------- SQLite

    [Fact]
    public void SqliteNeedsNoDatabaseCreation()
    {
        // Microsoft.Data.Sqlite creates the file on Open(). Measured in Pass 15, not assumed.
        Assert.False(LogDatabaseDdl.RequiresExplicitCreation(DbProviderKeys.SqLite));
        Assert.True(LogDatabaseDdl.RequiresExplicitCreation(DbProviderKeys.Npgsql));
        Assert.True(LogDatabaseDdl.RequiresExplicitCreation(DbProviderKeys.SqlServer));
    }

    [Fact]
    public void SqliteGetsItsParentDirectoryCreated()
    {
        // The one thing SQLite does NOT do for itself: it creates the file but not the folder, and
        // fails with "SQLite Error 14: unable to open database file" - which names neither the path
        // nor the reason.
        var root = Path.Combine(Path.GetTempPath(), "gx-logdb-ddl", Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "nested", "deeper", "logs.db");

        try
        {
            Assert.False(Directory.Exists(Path.GetDirectoryName(target)));

            LogDatabaseDdl.EnsureParentDirectoryExists(DbProviderKeys.SqLite, $"Data Source={target}");

            Assert.True(Directory.Exists(Path.GetDirectoryName(target)));
            Assert.False(File.Exists(target), "the directory is created; the file is still SQLite's job");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EnsureParentDirectoryIsANoOpForTheOtherProvidersAndForMemory()
    {
        // Called unconditionally by the startup check, so it has to be harmless everywhere.
        LogDatabaseDdl.EnsureParentDirectoryExists(DbProviderKeys.Npgsql, "Host=h;Database=d;Username=u");
        LogDatabaseDdl.EnsureParentDirectoryExists(DbProviderKeys.SqlServer, @"Server=h;Database=d;");
        LogDatabaseDdl.EnsureParentDirectoryExists(DbProviderKeys.SqLite, "Data Source=:memory:");
    }

    // ------------------------------------------------------------- unsupported providers

    [Fact]
    public void AnUnknownProviderIsRefusedRatherThanIgnored()
    {
        Assert.Throws<InvalidOperationException>(() => LogDatabaseDdl.RequiresExplicitCreation("oracle"));
        Assert.Throws<InvalidOperationException>(() => LogDatabaseDdl.ExistsCommandText("oracle"));
        Assert.Throws<InvalidOperationException>(() => LogDatabaseDdl.CreateStatement("oracle", "x"));
        Assert.Throws<InvalidOperationException>(() => LogDatabaseDdl.MaintenanceDatabase("oracle"));
    }

    [Fact]
    public void SqliteHasNoMaintenanceDatabaseAndSaysSo()
    {
        // Asking for one is a caller error, not a silent default: nothing should be reaching for a
        // maintenance connection on a provider whose database is a file.
        Assert.Throws<InvalidOperationException>(() => LogDatabaseDdl.MaintenanceDatabase(DbProviderKeys.SqLite));
        Assert.Throws<InvalidOperationException>(
            () => LogDatabaseDdl.CreateMaintenanceConnection(DbProviderKeys.SqLite, "Data Source=x.db"));
    }

    // ------------------------------------------------------------- helpers

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    /// <summary>
    /// A real <see cref="PostgresException"/> carrying a chosen SqlState. Its public constructor
    /// takes the fields the wire protocol carries, so this is the actual type the classifier sees.
    /// </summary>
    private static PostgresException PostgresError(string sqlState) =>
        new(messageText: "probe", severity: "ERROR", invariantSeverity: "ERROR", sqlState: sqlState);
}
