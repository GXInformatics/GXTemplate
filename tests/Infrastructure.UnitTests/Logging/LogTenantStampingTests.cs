using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Infrastructure.Extensions;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Logging;
using CleanArchitecture.Blazor.Infrastructure.Services.Identity;
using Serilog;
using Serilog.Events;
#if (UsePostgreSql)
using Serilog.Sinks.PostgreSQL;
using Serilog.Sinks.PostgreSQL.ColumnWriters;
#endif
using Xunit;

namespace CleanArchitecture.Blazor.Infrastructure.UnitTests.Logging;

/// <summary>
/// The log row records which tenant produced the event, and records <c>null</c> when no tenant
/// produced it.
/// </summary>
/// <remarks>
/// <b>Null is a value here, not a gap.</b> Startup, seeding, the bootstrap administrator banner,
/// Hangfire's heartbeats and anything logged after a circuit has gone all run with no ambient user
/// context. Those rows form a third partition - the installation's own events - and any future
/// per-tenant log view has to surface it rather than quietly dropping it.
/// <para>
/// <b>Why the source is the user context and not the HTTP context.</b> The other three enriched
/// values - UserName, ClientIP, ClientAgent - come from <c>IHttpContextAccessor</c> and therefore
/// exist only while a request does. A tenant is knowable wherever the ambient user context has been
/// pushed, which includes Blazor circuits, hub calls and mediator handlers running on continuations
/// long after the request completed. The test below pushes a context with no HTTP request anywhere
/// in sight, which is precisely the case the HTTP accessor could not have served.
/// </para>
/// <para>
/// <b>And why that works at all.</b> Serilog constructs enrichers itself, through a parameterless
/// constructor, and the logger is configured in <c>Program.cs</c> before <c>AddInfrastructure</c>
/// has registered anything - so the enricher cannot resolve a service. It reaches the ambient value
/// the same way it already reaches the request: by newing up an accessor whose state is static and
/// per-call-chain. <c>UserContextAccessor</c> was changed to make that true, and this test is what
/// holds it true.
/// </para>
/// </remarks>
public class LogTenantStampingTests
{
    private const string TenantId = "tenant-from-context";

#if (UseSqlServer || UsePostgreSql)
    // ------------------------------------------------------- configuration, the two server providers

#if (UseSqlServer)
    [Fact]
    public void TheSqlServerSink_WritesTheTenantColumn_AndAllowsItToBeNull()
    {
        var column = Assert.Single(
            SerilogExtensions.BuildSqlServerColumnOptions().AdditionalColumns!,
            c => c.ColumnName == "TenantId");

        Assert.Equal("TenantId", column.PropertyName);

        // AllowNull is the assertion that matters. Every startup row has no tenant, so a NOT NULL
        // column would make the sink reject the first event the application ever writes - and it
        // would do it asynchronously, into SelfLog, with the application looking healthy.
        Assert.True(column.AllowNull);
    }

#endif
#if (UsePostgreSql)
    [Fact]
    public void ThePostgresSink_ReadsTheEnrichedTenantProperty()
    {
        var writer = SerilogExtensions.BuildNpgsqlColumnWriters()["tenant_id"];

        var single = Assert.IsType<SinglePropertyColumnWriter>(writer);
        Assert.Equal("TenantId", single.Name);

        // Raw, matching user_name and client_ip. ToString would render a null as a quoted string
        // rather than leaving the column null, which would turn "no tenant" into a tenant named
        // "null" - and the installation partition would stop being distinguishable.
        Assert.Equal(PropertyWriteMethod.Raw, single.WriteMethod);
    }
#endif

#endif
    // End to end - the real PostgreSQL sink writing tenant_id into the real table, and NULL outside a
    // context - is LogTablePostgresTests.TheSinkWritesTheAmbientTenant_IntoTenantId (pass 47, CO-157).
    // It replaces the SQLite round trips that were here, which could only show the column staying NULL.

    // ------------------------------------------------------- the enricher, provider-independent

    /// <summary>Captures the enriched events rather than writing them anywhere.</summary>
    private sealed class CapturingSink : Serilog.Core.ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static LogEvent EnrichOneEvent(string? tenantId)
    {
        var capture = new CapturingSink();
        using (var logger = new LoggerConfiguration()
                   .MinimumLevel.Verbose()
                   .Enrich.WithUserInfo()
                   .WriteTo.Sink(capture)
                   .CreateLogger())
        {
            IUserContextAccessor accessor = new UserContextAccessor();
            using (tenantId is null
                       ? null
                       : accessor.Push(new UserContext("log-user", "logger", TenantId: tenantId)))
            {
                logger.Information("a probe row");
            }
        }

        return Assert.Single(capture.Events);
    }

    [Fact]
    public void TheEnricherPublishesTheAmbientTenant_WithNoHttpRequestInSight()
    {
        // The mechanism itself, independent of any sink or provider: this is what the SQL Server and
        // PostgreSQL writers asserted above go on to read.
        //
        // There is no HTTP context anywhere in this test, which is exactly the case
        // IHttpContextAccessor could not have served - a mediator handler on a continuation, a hub
        // call, a Blazor circuit. The accessor pushed here and the one the enricher constructs for
        // itself are different objects that agree, because the value belongs to the call chain.
        var logEvent = EnrichOneEvent(TenantId);

        var value = Assert.IsType<ScalarValue>(logEvent.Properties["TenantId"]);
        Assert.Equal(TenantId, value.Value);
    }

    [Fact]
    public void TheEnricherPublishesANullTenant_WhenThereIsNoAmbientContext()
    {
        // Startup, seeding, Hangfire heartbeats. The property is present and null rather than
        // absent, so the sinks bind a null instead of failing on a missing property.
        var logEvent = EnrichOneEvent(tenantId: null);

        var value = Assert.IsType<ScalarValue>(logEvent.Properties["TenantId"]);
        Assert.Null(value.Value);
    }
}
