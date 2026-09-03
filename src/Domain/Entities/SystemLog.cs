// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.


// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using CleanArchitecture.Blazor.Domain.Common.Entities;

namespace CleanArchitecture.Blazor.Domain.Entities;

public class SystemLog : IEntity<int>
{
    public string? Message { get; set; }
    public string? MessageTemplate { get; set; }
    public string Level { get; set; } = default!;

    public DateTime TimeStamp { get; set; } = DateTime.UtcNow;
    public string? Exception { get; set; }
    public string? UserName { get; set; }

    /// <summary>
    /// The tenant whose activity produced this event, or <c>null</c> for an installation event.
    /// </summary>
    /// <remarks>
    /// <b>Null is a first-class value here, not a gap.</b> Startup logging, database seeding, the
    /// bootstrap administrator banner, Hangfire's server heartbeats and any exception logged after
    /// a circuit has gone all run with no ambient user context, so they belong to the installation
    /// rather than to any tenant. Those rows form a third partition alongside the tenants, and any
    /// future per-tenant log view has to surface it rather than silently dropping it - a tenant
    /// administrator who cannot see that the application restarted is being shown an edited log.
    /// <para>
    /// <b>Adding this property was a five-part change.</b> The entity, <c>LogTableDdl</c>'s column
    /// arrays for all three providers, the SQL Server sink's AdditionalColumns, the PostgreSQL
    /// sink's writer dictionary, and <c>UserInfoEnricher</c>. They move together or the sink names
    /// a column the table does not have and every INSERT fails asynchronously into SelfLog while
    /// the application looks healthy. <c>SinkColumnDriftTests</c> exists to catch exactly that and
    /// needed no edit to catch this one - it derives its expectations from this entity.
    /// </para>
    /// <para>
    /// <b>And it had to happen before deployment.</b> This DDL creates a table and never alters
    /// one, so a log database provisioned before this property existed keeps its old columns
    /// forever and no guard here will touch it. Adding a property later means a hand-written ALTER
    /// against every deployed log database, per provider.
    /// </para>
    /// </remarks>
    public string? TenantId { get; set; }

    public string? ClientIP { get; set; }
    public string? ClientAgent { get; set; }
    public string? Properties { get; set; }
    public string? LogEvent { get; set; }
    public int Id { get; set; }
}
