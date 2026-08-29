using CleanArchitecture.Blazor.Application;
using CleanArchitecture.Blazor.Infrastructure;
using CleanArchitecture.Blazor.Infrastructure.Extensions;
using CleanArchitecture.Blazor.Server.UI;
using QuestPDF;
using QuestPDF.Infrastructure;

// FIRST LINE. Before WebApplication.CreateBuilder, therefore before RegisterSerilog, before any
// service registration, and before anything can construct a DbContext or write a log row.
ConfigureProcessWideState();

var builder = WebApplication.CreateBuilder(args);
builder.RegisterSerilog();
builder.WebHost.UseStaticWebAssets();

builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration)
    .AddServerUI(builder.Configuration);
var app = builder.Build();

app.ConfigureServer(builder.Configuration);

// After Serilog exists, and before the databases are touched, so the line an operator reads when a
// timestamp problem surfaces sits above the first stack trace rather than below it.
LogEffectiveProcessWideState();

await app.InitializeDatabaseAsync().ConfigureAwait(false);
await app.RunAsync().ConfigureAwait(false);

/// <summary>
/// Every decision this process makes that is global to the process, in one place, before anything
/// else runs.
/// </summary>
/// <remarks>
/// <b>Why this method exists at all.</b> Until Pass 14B the application set
/// <c>AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true)</c> from inside an
/// <c>AddDbContextFactory</c> options lambda in Infrastructure - a process-wide, permanent decision
/// taken lazily, conditionally, from deep inside a service registration. The defect was never the
/// switch itself; it was that a process-wide decision had nowhere visible to live, so it lived
/// wherever the code that needed it happened to be. That placement made it invisible in review and
/// made its effect depend on WHEN some unrelated component first built a DbContext: Npgsql caches
/// its type handlers on first use, so a Serilog write that beat EF to the driver left EF and the
/// converters disagreeing, with every write failing at runtime only.
/// <para>
/// So this method is a slot as much as it is code. Anything global to the process - an AppContext
/// switch, a static licence key, a default culture, a ServicePointManager setting - belongs here,
/// where it runs once, unconditionally, before the composition root, and where the next reader will
/// look for it. Nothing that reads configuration can live here, and that is the intended pressure:
/// a process-wide decision that depends on configuration is usually a per-scope decision wearing a
/// disguise.
/// </para>
/// <para>
/// The Npgsql switch is deliberately NOT here. It was deleted, not moved: under <c>timestamptz</c>
/// there is nothing to set, because Npgsql's default mapping for <c>DateTime</c> already is
/// <c>timestamp with time zone</c>. <c>ProcessWideStateTests</c> asserts it is unset after a real
/// boot, and <c>TimestamptzModelInvariantTests</c> fails if it returns by any route at all.
/// </para>
/// </remarks>
static void ConfigureProcessWideState()
{
    // Moved here from Server.UI/DependencyInjection.cs, where it sat midway through endpoint
    // mapping inside ConfigureServer. Same family as the Npgsql switch and far lower stakes - a
    // static, idempotent, read only when a PDF is generated, which is always later - but a
    // process-wide static assigned from a request-pipeline builder is the exact shape this method
    // exists to stop being normal.
    Settings.License = LicenseType.Community;
}

/// <summary>
/// One line naming the effective value of every process-wide decision, at Information.
/// </summary>
/// <remarks>
/// The staging outage this whole line of work came from had a signature nobody could read: EF
/// believing one column type and the driver's converters believing another, surfacing as one
/// failing dashboard. Stating the effective state out loud turns that into something an operator
/// greps for.
/// <para>
/// Marked as a log-database diagnostic, which excludes it from the DATABASE sink and leaves it to
/// console and file (Pass 11C's rule, asserted by <c>LogDatabaseDiagnosticRoutingTests</c>). Two
/// reasons, both sufficient: a message about how DateTimes bind to the database cannot be delivered
/// through the database if that binding is what is broken, and this runs before
/// <c>PrepareLogDatabaseAsync</c>, so the log table may not exist yet.
/// </para>
/// </remarks>
void LogEffectiveProcessWideState()
{
    const string legacyTimestampSwitch = "Npgsql.EnableLegacyTimestampBehavior";

    var provider = builder.Configuration["DatabaseSettings:DBProvider"] ?? "(unset)";
    var legacySet = AppContext.TryGetSwitch(legacyTimestampSwitch, out var legacyEnabled);

    // "In force" is a property of the provider AND the switch together: timestamptz is Npgsql's
    // default mapping, and the legacy switch is the only thing that takes it away. On MSSQL and
    // SQLite the question does not arise, and saying so is more useful than printing "false".
    var isNpgsql = string.Equals(provider, "postgresql", StringComparison.OrdinalIgnoreCase);
    var timestamptz = isNpgsql
        ? (legacySet && legacyEnabled ? "NO - the legacy switch is overriding it" : "yes")
        : "n/a (not PostgreSQL)";

    using var scope = app.Logger.BeginScope(new Dictionary<string, object>
    {
        [SerilogExtensions.LogDatabaseDiagnosticProperty] = true
    });

    app.Logger.LogInformation(
        "Process-wide state: database provider {Provider}; {Switch} {LegacySwitchState}; " +
        "timestamptz in force: {Timestamptz}; QuestPDF licence {QuestPdfLicence}.",
        provider,
        legacyTimestampSwitch,
        legacySet ? (legacyEnabled ? "SET to true" : "set to false") : "not set",
        timestamptz,
        Settings.License?.ToString() ?? "(unset)");
}

/// <summary>
/// Top-level statements compile to an internal Program class, which WebApplicationFactory cannot
/// use as its entry point. Declaring it public here is what lets the HTTP integration harness boot
/// THIS application - the real pipeline, the real middleware order - rather than a reconstruction
/// of it. Nothing else references this type.
/// </summary>
public partial class Program;
