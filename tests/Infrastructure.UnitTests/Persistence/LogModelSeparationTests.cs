using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Logging;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CleanArchitecture.Blazor.Infrastructure.UnitTests.Persistence;

/// <summary>
/// The model split, asserted where it is actually decided: in the two contexts' models.
/// </summary>
/// <remarks>
/// This is the pass's central claim reduced to something a test can hold. If SystemLog is in
/// ApplicationDbContext's model then EF's migration creates a SystemLogs table in the business
/// database, whatever the intention was - and the whole point of moving logs to their own database
/// (keeping log volume out of the business backup) is lost silently.
/// <para>
/// It is easy to get this wrong in a way that looks right. Removing the DbSet is NOT sufficient:
/// <c>ApplyConfigurationsFromAssembly</c> calls <c>builder.Entity&lt;T&gt;()</c> for every
/// configuration it finds, so an unfiltered scan of the Infrastructure assembly re-adds SystemLog
/// from SystemLogConfiguration alone. The namespace predicate on each context is what prevents that,
/// and this test is what stops someone "tidying" the predicate away.
/// </para>
/// </remarks>
public class LogModelSeparationTests
{
    // The provider only has to be enough to build a model; nothing here opens a connection.
    private static ApplicationDbContext BusinessContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite("Data Source=:memory:").Options);

    private static LogDbContext LogContext() =>
        new(new DbContextOptionsBuilder<LogDbContext>()
            .UseSqlite("Data Source=:memory:").Options);

    [Fact]
    public void TheBusinessModel_DoesNotContainSystemLog()
    {
        using var db = BusinessContext();

        Assert.Null(db.Model.FindEntityType(typeof(SystemLog)));
    }

    [Fact]
    public void TheBusinessModel_StillContainsAuditTrail()
    {
        // The Pass 11 scope boundary, stated as an assertion rather than a promise: this pass moved
        // logs and nothing else. The audit trail stays in the business database, written in the same
        // transaction as the change it records (Pass 5).
        using var db = BusinessContext();

        Assert.NotNull(db.Model.FindEntityType(typeof(AuditTrail)));
    }

    [Fact]
    public void TheLogModel_ContainsSystemLog()
    {
        using var db = LogContext();

        Assert.NotNull(db.Model.FindEntityType(typeof(SystemLog)));
    }

    [Fact]
    public void TheLogModel_ContainsNothingElse()
    {
        // "No query can accidentally join across databases" is only true while this holds. A single
        // business entity leaking into this model would make such a join expressible, and it would
        // type-check, and it would fail only at runtime against a database with one table.
        using var db = LogContext();

        var entities = db.Model.GetEntityTypes().Select(e => e.ClrType).ToArray();

        Assert.Equal([typeof(SystemLog)], entities);
    }

    [Fact]
    public void TheLogConfiguration_IsInItsOwnNamespace_WhichIsWhatThePredicatesMatchOn()
    {
        // The two predicates compare namespaces for equality. If these two ever became equal - by a
        // move, a rename, or a helpful refactor - both scans would pick up both sets and SystemLog
        // would silently return to the business model. Asserting they differ makes that visible here
        // rather than in a migration nobody reads.
        Assert.NotEqual(ApplicationDbContext.ConfigurationsNamespace, LogDbContext.ConfigurationsNamespace);
    }
}

/// <summary>
/// The table name the log model resolves to, per provider.
/// </summary>
/// <remarks>
/// This exists because getting it wrong is silent and specific. EF takes a table name from the DbSet
/// PROPERTY name, and <c>ILogDbContext</c> exposes an <c>IQueryable</c> on purpose, so without an
/// explicit <c>ToTable</c> the model resolves to "SystemLog" - singular - while every sink writes to
/// "SystemLogs". The log database is then healthy and full of rows that the page cannot read.
/// </remarks>
public class LogTableNamingTests
{
    [Fact]
    public void OnSqlite_TheModelReadsSystemLogs()
    {
        using var db = new LogDbContext(new DbContextOptionsBuilder<LogDbContext>()
            .UseSqlite("Data Source=:memory:").Options);

        Assert.Equal("SystemLogs", db.Model.FindEntityType(typeof(SystemLog))!.GetTableName());
    }

    [Fact]
    public void OnSqlServer_TheModelReadsSystemLogs()
    {
        using var db = new LogDbContext(new DbContextOptionsBuilder<LogDbContext>()
            .UseSqlServer("Server=none;Database=none;").Options);

        Assert.Equal("SystemLogs", db.Model.FindEntityType(typeof(SystemLog))!.GetTableName());
    }

    [Fact]
    public void OnPostgres_TheModelReadsTheSnakeCaseNameTheSinkWrites()
    {
        // UseSnakeCaseNamingConvention is applied by UseDatabase for this provider, and the
        // PostgreSQL sink writes to system_logs. The two have to meet in the middle.
        using var db = new LogDbContext(new DbContextOptionsBuilder<LogDbContext>()
            .UseNpgsql("Host=none;Database=none;")
            .UseSnakeCaseNamingConvention().Options);

        Assert.Equal(
            CleanArchitecture.Blazor.Infrastructure.Extensions.SerilogExtensions.NpgsqlTableName,
            db.Model.FindEntityType(typeof(SystemLog))!.GetTableName());
    }
}
