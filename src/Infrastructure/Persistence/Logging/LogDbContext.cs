// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using CleanArchitecture.Blazor.Infrastructure.Extensions;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Logging.Configurations;

namespace CleanArchitecture.Blazor.Infrastructure.Persistence.Logging;

/// <summary>
/// The reading side of the log database. Serilog's sink owns writing and owns the table; this
/// context only ever queries it, plus the one purge the SystemLogs page offers.
/// </summary>
/// <remarks>
/// It is a separate <see cref="DbContext"/> TYPE rather than <see cref="ApplicationDbContext"/> on a
/// second connection, and the distinction is structural rather than stylistic. The business context
/// carries the whole business model - Identity, Tenants, AuditTrails, Documents,
/// DataProtectionKeys - so pointed at the log database every one of those would be nominally
/// addressable there: a query joining SystemLogs to AuditTrails would compile, type-check and
/// produce SQL, failing only at runtime against a database with one table. Worse,
/// <c>ApplicationDbContextInitializer</c> calls <c>Database.MigrateAsync()</c> on that type, so one
/// registration mistake would migrate the entire business schema into the log database.
/// <para>
/// Two types make those states unrepresentable instead of merely unlikely, which is what
/// "nothing can accidentally join across databases" has to mean to be worth asserting.
/// </para>
/// <para>
/// This context owns NO schema. It has no migrations and never creates anything: the sink creates
/// the table (see <c>SerilogExtensions</c>), and the two agree because the sink's column
/// configuration and this model are two expressions of the same <see cref="SystemLog"/> entity -
/// which <c>SinkColumnDriftTests</c> pins for every provider.
/// </para>
/// </remarks>
public class LogDbContext : DbContext, ILogDbContext
{
    public LogDbContext(DbContextOptions<LogDbContext> options)
        : base(options)
    {
        // Nothing here is ever saved through the change tracker - the sink writes, this side reads -
        // so tracking would be pure overhead on every page of a table that grows without bound.
        ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
    }

    /// <summary>
    /// The log model's configuration namespace. Both this context and
    /// <see cref="ApplicationDbContext"/> scan the same assembly, so each must filter to its own.
    /// </summary>
    public static readonly string ConfigurationsNamespace = typeof(SystemLogConfiguration).Namespace!;

    /// <inheritdoc />
    /// <remarks>
    /// Deliberately an <see cref="IQueryable{T}"/> rather than a <c>DbSet</c>: the interface must not
    /// hand the business layer anything it can write through. <c>Set&lt;SystemLog&gt;()</c> stays
    /// private to this type, where the purge below is the only thing that uses it destructively.
    /// </remarks>
    public IQueryable<SystemLog> SystemLogs => Set<SystemLog>();

    /// <inheritdoc />
    public Task<int> PurgeAsync(CancellationToken cancellationToken = default) =>
        Set<SystemLog>().ExecuteDeleteAsync(cancellationToken);

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Equality, not StartsWith - see SystemLogConfiguration's remarks for why the partition of
        // this scan is load-bearing in both directions.
        builder.ApplyConfigurationsFromAssembly(
            Assembly.GetExecutingAssembly(),
            t => t.Namespace == ConfigurationsNamespace);

        // The table name, stated explicitly and per provider. Two traps meet here, and both are
        // silent.
        //
        // First, EF derives a table name from the DbSet PROPERTY name, and ILogDbContext exposes an
        // IQueryable rather than a DbSet on purpose. With nothing said, EF falls back to the entity
        // name and looks for "SystemLog" - singular - while every sink writes "SystemLogs". The log
        // database is then healthy, full, and unreadable.
        //
        // Second, naming it once for all providers does not work either: ToTable marks the name as
        // explicitly configured, and UseSnakeCaseNamingConvention then leaves it alone. A blanket
        // ToTable("SystemLogs") therefore reads the right table on SQLite and SQL Server and the
        // wrong one on PostgreSQL, whose sink writes system_logs. That is a provider-specific
        // failure invisible to any single-provider test, which is why LogTableNamingTests covers all
        // three.
        builder.Entity<SystemLog>().ToTable(
            Database.IsNpgsql() ? SerilogExtensions.NpgsqlTableName : "SystemLogs");
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        // Matches ApplicationDbContext, which is where this entity's model used to live. It no
        // longer affects any DDL - the sink creates the table now - but keeping the conventions
        // identical keeps the model identical, and the model is what the drift test compares
        // against the sink's columns.
        configurationBuilder.Properties<string>().HaveMaxLength(450);
    }
}
