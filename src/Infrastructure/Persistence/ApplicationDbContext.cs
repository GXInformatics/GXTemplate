// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using CleanArchitecture.Blazor.Domain.Common.Entities;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Configurations;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using CleanArchitecture.Blazor.Application.Common.Constants;

namespace CleanArchitecture.Blazor.Infrastructure.Persistence;

#nullable disable
public class ApplicationDbContext : IdentityDbContext<
    ApplicationUser, ApplicationRole, string,
    ApplicationUserClaim, ApplicationUserRole, ApplicationUserLogin,
    ApplicationRoleClaim, ApplicationUserToken>, IApplicationDbContext, IDataProtectionKeyContext
{
    // The file is #nullable disable (the Identity base class predates annotations), so the
    // tenancy members opt back in locally: their nullability is the point - a null accessor
    // and a null tenant are distinct, meaningful states documented on CurrentTenantId.
#nullable enable
    private readonly IUserContextAccessor? _userContextAccessor;

    /// <param name="userContextAccessor">
    /// The ambient principal, used only by <see cref="CurrentTenantId"/>.
    /// </param>
    /// <remarks>
    /// <b>Optional, and it has to be.</b> Seventeen places construct this context directly with
    /// nothing but options - the interceptor suites among them, which Pass 5 and Pass 24 require to
    /// stay byte-unmodified. A required parameter would have rewritten all of them, so the
    /// dependency is optional and its absence means the same thing as "no ambient principal": the
    /// context sees installation-level rows. There is deliberately NO special case making an absent
    /// accessor unfiltered - a test path and a production path that disagree about a security
    /// boundary is how the boundary stops being one.
    /// <para>
    /// EF resolves this from the container when the context comes from
    /// <c>IDbContextFactory</c>, which is how the application always builds one.
    /// </para>
    /// </remarks>
    public ApplicationDbContext(
        DbContextOptions<ApplicationDbContext> options,
        IUserContextAccessor? userContextAccessor = null)
        : base(options)
    {
        _userContextAccessor = userContextAccessor;
    }

    /// <summary>
    /// The tenant every filtered read is scoped to, resolved fresh on each query.
    /// </summary>
    /// <remarks>
    /// <b>A member on the context, not a captured local - and the distinction is the whole
    /// feature.</b> A query filter's expression is compiled into the model, and the model is cached
    /// once per context type for the life of the process. A filter closing over a LOCAL would bake
    /// the first request's tenant into every subsequent request forever. A filter referencing a
    /// member of the context instance is re-evaluated per instance, because EF parameterises the
    /// subtree rooted at the context and binds it at execution time.
    /// <para>
    /// Pass 29 proved this against EF 10.0.11 rather than trusting it: two contexts built from one
    /// cached model, with different ambient tenants, returned different rows.
    /// </para>
    /// <para>
    /// <b>Null is a real value here, and it is not "unscoped".</b> With no ambient principal -
    /// seeding, bootstrap, a directly constructed context - this is null, and EF's null-semantics
    /// rewriting turns the comparison into <c>TenantId IS NULL</c> rather than
    /// <c>TenantId = @p</c>. So such a context sees exactly the installation-level rows: what
    /// seeding needs, and why no infrastructure path requires an exemption. It does not see
    /// everything, and it does not see nothing.
    /// </para>
    /// </remarks>
    private string? CurrentTenantId => _userContextAccessor?.Current?.TenantId;
#nullable restore

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

        // NAMED since Pass 29. The single-argument overload REPLACES any filter already on an
        // entity, so with a second filter below this one had to be named or one of the two would
        // vanish silently. (It also currently matches nothing: no entity derives from
        // BaseAuditableSoftDeleteEntity. Kept so it composes correctly the day one does.)
        builder.ApplyGlobalFilters<ISoftDelete>(
            QueryFilters.SoftDelete, s => s.DeletedAt == null);

        // The tenant filter, by EXPLICIT ENTITY LIST rather than by marker interface.
        //
        // IMayHaveTenant looks like the right key and is not. AuditTrail does not implement it -
        // the interceptor CONSTRUCTS audit rows with a TenantId rather than stamping them through
        // the marker - so a marker-driven filter would have missed the one entity this exists for.
        // And it would have caught Document, which VisibleDocumentSpecification already scopes by
        // an owner-or-tenant rule a global filter cannot express; scoping it twice would be two
        // rules free to disagree, not additive safety.
        //
        // So the list is written out, and adding to it is a deliberate act rather than a side
        // effect of implementing an interface.
        //
        // ONE FILTER NAME, TWO PREDICATES - and that is correct, not an oversight.
        //
        // There is no shared expression to reuse here: HasQueryFilter takes a lambda per entity, so
        // each entry below states its own rule. That matters because a null TenantId means opposite
        // things for the two entities on the list, and a single shared predicate would have forced
        // one of them to be wrong.
        //
        //   AuditTrail    - a row is an EVENT that happened in exactly one tenant. A null tenant is
        //                   an installation-level event (seeding, bootstrap, background work) and
        //                   belongs to nobody, so strict equality is right: a tenant sees its own
        //                   events and a context with no principal sees the installation's.
        //   PicklistSet   - a row is REFERENCE DATA. A null tenant means "everyone's", so the
        //                   predicate admits it alongside the caller's own rows. Shared plus
        //                   per-tenant additions: every shipped picklist stays visible to every
        //                   tenant with no per-tenant seeding path, and a tenant's own additions
        //                   stay private to it.
        //
        // The NAME is shared deliberately: QueryFilters.Tenant is what an exemption names, and an
        // exemption means the same thing for both - "read across tenants, having checked a right".
        builder.Entity<AuditTrail>().HasQueryFilter(
            QueryFilters.Tenant,
            (AuditTrail a) => a.TenantId == CurrentTenantId);

        builder.Entity<PicklistSet>().HasQueryFilter(
            QueryFilters.Tenant,
            (PicklistSet p) => p.TenantId == null || p.TenantId == CurrentTenantId);

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
