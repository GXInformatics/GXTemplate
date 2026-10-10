// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Linq.Expressions;
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

        // THE TENANT FILTER, BY MARKER INTERFACE (Pass 54). Before this pass it was an explicit list
        // of two entities, so a project entity implementing IMustHaveTenant was stamped on insert and
        // then readable by every tenant - the marker promised isolation the model did not deliver.
        // Now implementing the marker IS the decision, and TenantFilterGuard fails startup if any
        // marked entity ends up without the filter.
        //
        // ONE FILTER NAME, TWO PREDICATES, because a null TenantId means different things:
        //
        //   IMustHaveTenant - TenantId == current. A row belongs to exactly one tenant. With no
        //                     principal (current is null) EF's null semantics make this
        //                     TenantId IS NULL, which a must-have row never is: no principal, no rows.
        //   IMayHaveTenant  - TenantId == null || TenantId == current. A null tenant is the SHARED,
        //                     installation-level partition (IMayBeShared is how a row is put there
        //                     deliberately), visible to everyone beside the caller's own rows.
        //
        // Document joined the filter here. It used to be left out because VisibleDocumentSpecification
        // scopes it by owner-or-tenant and "two rules free to disagree" was the worry; they cannot
        // disagree in the permissive direction, since a global filter only ever narrows. For a
        // principal WITH a tenant the conjunction is exactly the specification's rule. For one
        // WITHOUT, the filter confines them to tenantless documents - closing the specification's
        // no-tenant branch, which served every tenant's public documents (pass 46, F1) - without
        // touching the specification, which pass 49 owns.
        //
        // The NAME is shared deliberately: QueryFilters.Tenant is what an exemption names, and an
        // exemption means the same thing everywhere - "read across tenants, having checked a right".
        ApplyTenantFilters(builder);

        // AuditTrail is the ONE explicit registration left, and it is not an oversight. Its rows are
        // CONSTRUCTED by AuditableEntityInterceptor with the tenant the change was made in; they are
        // not stamped through a marker, so it implements neither. Its rule is strict equality on a
        // NULLABLE column - a null tenant is an installation-level EVENT (seeding, bootstrap,
        // background work) that belongs to nobody, so it must not be shown to every tenant the way
        // IMayHaveTenant's null-or-equal would show it, and IMustHaveTenant's non-null column would
        // forbid recording it at all. TenantFilterGuard checks this registration too.
        builder.Entity<AuditTrail>().HasQueryFilter(
            QueryFilters.Tenant,
            (AuditTrail a) => a.TenantId == CurrentTenantId);

        // LAST, and after ApplyConfigurationsFromAssembly: the GX naming standard yields to an
        // explicit ToTable, so the configurations have to have been applied before it runs. It maps
        // every IBusinessEntity - i.e. everything deriving from BaseEntity - to
        // core."TBL_UPPER_SNAKE", and leaves this template's own tables (Identity, Tenants,
        // AuditTrails, Documents, PicklistSets, DataProtectionKeys, __EFMigrationsHistory) in the
        // default schema under their existing names. See GxNamingConventions.
        builder.ApplyGxTableNaming();
    }

    /// <summary>
    /// Registers <see cref="QueryFilters.Tenant"/> on every hierarchy root implementing
    /// <see cref="IMustHaveTenant"/> (strict) or <see cref="IMayHaveTenant"/> (null-or-equal).
    /// </summary>
    /// <remarks>
    /// <b>The comparison is built against <c>this</c>, not a captured value.</b> The expression
    /// below is exactly what the compiler emits for <c>e =&gt; e.TenantId == CurrentTenantId</c>
    /// written in an instance method: a property read on a constant holding the context. EF
    /// recognises that constant and rebinds it to the executing context on every query, which is
    /// what keeps the cached model from baking in the first request's tenant (see
    /// <see cref="CurrentTenantId"/>). Built by hand only because the entity type is not known at
    /// compile time.
    /// <para>
    /// Owned types are skipped because they cannot carry a filter (their owner's applies), and TPH
    /// leaves because EF only accepts a filter on the root. A marked leaf under an unmarked root is
    /// therefore NOT filtered - <see cref="TenantFilterGuard"/> refuses that model at startup and says
    /// to move the marker to the root.
    /// </para>
    /// </remarks>
    private void ApplyTenantFilters(ModelBuilder builder)
    {
        var self = Expression.Constant(this, typeof(ApplicationDbContext));
        var current = Expression.Property(self, CurrentTenantIdProperty);

        foreach (var entityType in builder.Model.GetEntityTypes().ToList())
        {
            if (entityType.IsOwned() || entityType.BaseType is not null) continue;

            var clr = entityType.ClrType;
            var mustHave = typeof(IMustHaveTenant).IsAssignableFrom(clr);
            if (!mustHave && !typeof(IMayHaveTenant).IsAssignableFrom(clr)) continue;

            var e = Expression.Parameter(clr, "e");
            var tenant = TenantIdOf(e, mustHave ? typeof(IMustHaveTenant) : typeof(IMayHaveTenant));
            Expression body = Expression.Equal(tenant, current);
            if (!mustHave)
            {
                body = Expression.OrElse(Expression.Equal(tenant, Expression.Constant(null, typeof(string))), body);
            }

            builder.Entity(clr).HasQueryFilter(QueryFilters.Tenant, Expression.Lambda(body, e));
        }
    }

    private static readonly PropertyInfo CurrentTenantIdProperty =
        typeof(ApplicationDbContext).GetProperty(nameof(CurrentTenantId), BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>
    /// <c>e.TenantId</c> through the class's own property when it has one - what the filter would say
    /// if written by hand - and through the marker otherwise (an explicit interface implementation).
    /// </summary>
    private static Expression TenantIdOf(ParameterExpression e, Type marker)
    {
        var own = e.Type.GetProperty(nameof(IMayHaveTenant.TenantId), BindingFlags.Instance | BindingFlags.Public);
        return own is not null
            ? Expression.Property(e, own)
            : Expression.Property(Expression.Convert(e, marker), marker.GetProperty(nameof(IMayHaveTenant.TenantId))!);
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
