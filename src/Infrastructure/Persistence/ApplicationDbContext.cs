// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using CleanArchitecture.Blazor.Domain.Common.Entities;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Configurations;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;

namespace CleanArchitecture.Blazor.Infrastructure.Persistence;

#nullable disable
public class ApplicationDbContext : IdentityDbContext<
    ApplicationUser, ApplicationRole, string,
    ApplicationUserClaim, ApplicationUserRole, ApplicationUserLogin,
    ApplicationRoleClaim, ApplicationUserToken>, IApplicationDbContext, IDataProtectionKeyContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<Tenant> Tenants { get; set; }
    public DbSet<TenantUser> TenantUsers { get; set; }
    public DbSet<AuditTrail> AuditTrails { get; set; }
    public DbSet<Document> Documents { get; set; }

    public DbSet<PicklistSet> PicklistSets { get; set; }
    public DbSet<SecurityPolicy> SecurityPolicies { get; set; }
    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; }

    /// <summary>
    /// The business model's entity configurations. Anything outside this namespace - today, the
    /// log model under <c>Persistence.Logging.Configurations</c> - is deliberately not part of this
    /// context.
    /// </summary>
    public static readonly string ConfigurationsNamespace = typeof(AuditTrailConfiguration).Namespace!;

    protected override void OnModelCreating(ModelBuilder builder)
    {

        base.OnModelCreating(builder);

        // The predicate is load-bearing. ApplyConfigurationsFromAssembly calls builder.Entity<T>()
        // for every IEntityTypeConfiguration<T> it finds, which ADDS T to the model - so an
        // unfiltered scan of this assembly would re-add SystemLog here however thoroughly its DbSet
        // is removed, and the migration would go on creating a SystemLogs table in the business
        // database. Equality, not StartsWith: the log configurations live in a namespace nested
        // under this one's parent, and a prefix match would let them back in.
        builder.ApplyConfigurationsFromAssembly(
            Assembly.GetExecutingAssembly(),
            t => t.Namespace == ConfigurationsNamespace);

        builder.ApplyGlobalFilters<ISoftDelete>(s => s.DeletedAt == null);

        // LAST, and after ApplyConfigurationsFromAssembly: the GX naming standard yields to an
        // explicit ToTable, so the configurations have to have been applied before it runs. It maps
        // every IBusinessEntity - i.e. everything deriving from BaseEntity - to
        // core."TBL_UPPER_SNAKE", and leaves this template's own tables (Identity, Tenants,
        // AuditTrails, Documents, PicklistSets, DataProtectionKeys, __EFMigrationsHistory) in the
        // default schema under their existing names. See GxNamingConventions.
        builder.ApplyGxTableNaming();
    }
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.Properties<string>().HaveMaxLength(450);
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {

    }
}
