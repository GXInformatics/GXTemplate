#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application;
using CleanArchitecture.Blazor.Infrastructure;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Persistence;

/// <summary>
/// Every <c>DateTime</c> the business database persists maps to <c>timestamp with time zone</c>
/// under Npgsql.
/// </summary>
/// <remarks>
/// This is Pass 14's §C.1 probe promoted to a test, and it is the assertion that makes the
/// <c>timestamptz</c> decision self-enforcing rather than merely made. Npgsql's DEFAULT mapping for
/// <c>DateTime</c> is <c>timestamptz</c>; exactly one thing takes that away, and it is
/// <c>AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true)</c>, which used to be set
/// from inside an <c>AddDbContextFactory</c> options lambda in Infrastructure. So this test fails if
/// anyone reintroduces the switch <b>by any route</b> - and also if anyone reaches the same wrong
/// end by a different one, such as a convention or a per-property <c>HasColumnType</c>.
/// <para>
/// <b>The model is built through the production registration</b>, not through a hand-rolled
/// <c>DbContextOptionsBuilder</c>, and that is the whole design of the test. The switch is a
/// process-wide global read when Npgsql builds its type mappings; a test that configured its own
/// options would never run the code that sets it, so it would pass with the defect fully present.
/// Going through <c>AddInfrastructure</c> means the options lambda under test is the one production
/// uses. No server is contacted: EF builds a model from the provider and the entity configuration
/// alone, so the connection string below is deliberately unreachable.
/// </para>
/// <para>
/// <c>timestamptz</c> ACCEPTS <c>Kind=Utc</c> and REJECTS <c>Kind=Unspecified</c> and
/// <c>Kind=Local</c>; <c>timestamp without time zone</c> does the exact opposite. There is no value
/// that satisfies both, which is why this is worth pinning rather than leaving to review.
/// </para>
/// </remarks>
[TestFixture]
public class TimestamptzModelInvariantTests
{
    private const string LegacyTimestampSwitch = "Npgsql.EnableLegacyTimestampBehavior";
    private const string Timestamptz = "timestamp with time zone";

    private ServiceProvider _provider = null!;

    [SetUp]
    public void SetUp()
    {
        // The application's OWN appsettings.json, not a reconstruction of it. AddInfrastructure
        // binds IdentitySettings and AppConfigurationSettings and throws if either section is
        // missing, and hand-writing them here would mean this test drifts from the real defaults the
        // moment one changes. Only the provider and the two connection strings are overridden.
        //
        // The connection strings are unreachable on purpose: nothing here opens a connection - EF
        // builds a model from the provider and the entity configuration alone - and a reachable one
        // would invite this test to start depending on a server being installed.
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(SourceRoot(), "Server.UI", "appsettings.json"), optional: false)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DatabaseSettings:DBProvider"] = DbProviderKeys.Npgsql,
                ["DatabaseSettings:ConnectionString"] = "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;",
                ["DatabaseSettings:LogConnectionString"] = "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.None));
        services.AddSingleton<IConfiguration>(configuration);
        // AddApplication too, because AuditableEntityInterceptor and DispatchDomainEventsInterceptor
        // are constructor-injected into the options lambda and take IMediator from it. That is the
        // point rather than an inconvenience: the lambda this test exercises is the production one,
        // dependencies and all.
        services.AddApplication();
        services.AddInfrastructure(configuration);

        _provider = services.BuildServiceProvider();
    }

    [TearDown]
    public void TearDown() => _provider?.Dispose();

    [Test]
    public void EveryPersistedDateTime_MapsToTimestamptz()
    {
        var offenders = DateTimeColumns()
            .Where(c => !string.Equals(c.ColumnType, Timestamptz, StringComparison.OrdinalIgnoreCase))
            .Select(c => $"{c.Table}.{c.Name} is {c.ColumnType}")
            .ToArray();

        offenders.Should().BeEmpty(
            "every DateTime column must be {0}. Npgsql maps DateTime that way by default, so the " +
            "only ways to get here are the {1} AppContext switch, a convention, or a per-property " +
            "HasColumnType. A DateTime column that is not {0} rejects every Kind=Utc value the " +
            "application writes, including the bootstrap administrator's",
            Timestamptz, LegacyTimestampSwitch);
    }

    [Test]
    public void TheModelActuallyHasDateTimeColumnsToCheck()
    {
        // Without this, deleting every DateTime property - or breaking the walk below - would turn
        // the assertion above into a test of an empty sequence, which passes forever. Pass 14
        // counted eleven date columns; Pass 14B deleted the dead RefreshTokenExpiryTime, leaving
        // ten DateTime plus lockout_end, which is a DateTimeOffset and already timestamptz.
        DateTimeColumns().Should().HaveCountGreaterThanOrEqualTo(9,
            "the model walk must be finding the date columns; if it finds almost none, the walk " +
            "broke and EveryPersistedDateTime_MapsToTimestamptz is silently vacuous");
    }

    [Test]
    public void BuildingTheModelDidNotSetTheLegacySwitch()
    {
        // The direct statement of the ruling, in the one process where the production registration
        // has actually run. AddInfrastructure's options lambda is lazy, so it only executes when a
        // context is created - which DateTimeColumns() above does.
        _ = DateTimeColumns().ToArray();

        var set = AppContext.TryGetSwitch(LegacyTimestampSwitch, out var enabled);

        (set && enabled).Should().BeFalse(
            "{0} was deleted rather than moved in Pass 14B: under timestamptz there is nothing to " +
            "set, and setting it lazily from a DbContext options lambda is what produced the split " +
            "brain Pass 14 measured", LegacyTimestampSwitch);
    }

    [Test]
    public void NoSourceFileOutsideProgramSetsAnAppContextSwitch()
    {
        // The cheapest of Pass 14 F.3's three assertions, and the one that catches a reintroduction
        // BEFORE it has any effect - the model assertion above only sees a switch that some code
        // path actually executed, and a switch set in a branch this test does not reach would slip
        // past it. Program.cs is the sanctioned home (ConfigureProcessWideState), so it is the sole
        // exemption; today even it sets none.
        var offenders = Directory
            .EnumerateFiles(SourceRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => !string.Equals(Path.GetFileName(f), "Program.cs", StringComparison.Ordinal))
            .Where(f => File.ReadLines(f).Any(SetsASwitch))
            .Select(f => Path.GetRelativePath(SourceRoot(), f))
            .ToArray();

        offenders.Should().BeEmpty(
            "a process-wide switch belongs in ConfigureProcessWideState in Program.cs, which runs " +
            "once, unconditionally, before the composition root. Set anywhere else it runs lazily " +
            "and conditionally, and Npgsql caches its type handlers on first use, so whether it " +
            "takes effect depends on what touched the driver first");
    }

    /// <summary>
    /// True for a line that actually calls <c>AppContext.SetSwitch</c>, as opposed to one that
    /// talks about it.
    /// </summary>
    /// <remarks>
    /// Comments are excluded deliberately. <c>UseDatabase</c> carries a long note explaining why the
    /// Npgsql legacy switch is NOT set there and what happens if someone puts it back, and a scan
    /// that tripped over its own documentation would either fail permanently or - far worse - be
    /// silenced by deleting the explanation. Crude line-prefix matching is enough: this is a
    /// tripwire, not a compiler, and the code it is looking for is a bare static call.
    /// </remarks>
    private static bool SetsASwitch(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
            trimmed.StartsWith("*", StringComparison.Ordinal)) return false;

        return trimmed.Contains("AppContext.SetSwitch", StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the model walk

    private sealed record Column(string Table, string Name, string ColumnType);

    /// <summary>
    /// Every <c>DateTime</c> / <c>DateTime?</c> property in the model, with the column type the
    /// Npgsql provider gives it.
    /// </summary>
    private IEnumerable<Column> DateTimeColumns()
    {
        using var context = _provider
            .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
            .CreateDbContext();

        return context.Model.GetEntityTypes()
            .SelectMany(e => e.GetProperties()
                .Where(p => Nullable.GetUnderlyingType(p.ClrType) is null
                    ? p.ClrType == typeof(DateTime)
                    : Nullable.GetUnderlyingType(p.ClrType) == typeof(DateTime))
                .Select(p => new Column(
                    e.GetTableName() ?? e.Name,
                    p.GetColumnName(),
                    p.GetColumnType() ?? "(none)")))
            .ToArray();
    }

    /// <summary>
    /// The repository's <c>src</c> directory, found by walking up from the test assembly.
    /// </summary>
    /// <remarks>
    /// Anchored on the folder layout rather than on a solution file or a namespace, because both
    /// are renamed when the template is generated and the layout is not - so a generated project
    /// runs this assertion over its own sources.
    /// </remarks>
    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(
            Path.GetDirectoryName(typeof(TimestamptzModelInvariantTests).Assembly.Location)!);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src");
            if (Directory.Exists(Path.Combine(candidate, "Infrastructure"))) return candidate;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not find the src directory above " +
            typeof(TimestamptzModelInvariantTests).Assembly.Location +
            ". This assertion scans the sources rather than trusting them; it fails rather than " +
            "silently scanning nothing.");
    }
}
