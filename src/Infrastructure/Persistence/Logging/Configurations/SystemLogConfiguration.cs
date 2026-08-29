// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CleanArchitecture.Blazor.Infrastructure.Persistence.Logging.Configurations;

/// <summary>
/// The log model's only entity configuration.
/// </summary>
/// <remarks>
/// The NAMESPACE is load-bearing, not organisational. Both contexts call
/// <see cref="Microsoft.EntityFrameworkCore.ModelBuilder.ApplyConfigurationsFromAssembly(System.Reflection.Assembly, System.Func{System.Type, bool})"/>
/// over the same Infrastructure assembly, and that method does not merely configure entities it
/// already knows about - for every <c>IEntityTypeConfiguration&lt;T&gt;</c> it finds it calls
/// <c>builder.Entity&lt;T&gt;()</c>, which ADDS T to the model. So deleting
/// <c>DbSet&lt;SystemLog&gt;</c> from <see cref="ApplicationDbContext"/> is not sufficient on its
/// own: while this class sat beside the business configurations, the business context's scan put
/// SystemLog straight back into its model and the migration went on creating a SystemLogs table in
/// the business database - the exact outcome Pass 11 exists to prevent.
/// <para>
/// Each context now passes a predicate matching its own configuration namespace exactly (equality,
/// not prefix). <c>ApplicationDbContextHasNoLogModelTests</c> pins the result.
/// </para>
/// </remarks>
public class SystemLogConfiguration : IEntityTypeConfiguration<SystemLog>
{
    public void Configure(EntityTypeBuilder<SystemLog> builder)
    {
        // The table NAME is not set here: it depends on the provider, which an
        // IEntityTypeConfiguration cannot see. LogDbContext.OnModelCreating sets it - see the
        // comment there, which is worth reading before changing either.
        builder.Property(x => x.Level).HasMaxLength(450);
        builder.Property(x => x.Message).HasMaxLength(int.MaxValue);
        builder.Property(x => x.Exception).HasMaxLength(int.MaxValue);
        builder.Property(x => x.MessageTemplate).HasMaxLength(int.MaxValue);
        builder.Property(x => x.Properties).HasMaxLength(int.MaxValue);
        builder.Property(x => x.LogEvent).HasMaxLength(int.MaxValue);
        builder.HasIndex(x => new { x.Level });
        builder.HasIndex(x => x.TimeStamp);
    }
}
