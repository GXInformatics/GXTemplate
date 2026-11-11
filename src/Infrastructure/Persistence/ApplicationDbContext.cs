// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Linq.Expressions;
using System.Reflection;
using CleanArchitecture.Blazor.Domain.Common.Entities;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Configurations;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Rules;
using Microsoft.EntityFrameworkCore.Query;

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

    /// <summary>
    /// Whether the ambient principal reads every branch (Pass 59): <c>Permissions.Branches.ViewAll</c>, or
    /// the system context. A context member for the same reason as <see cref="CurrentTenantId"/>: EF
    /// re-binds it per query, so the cached model never bakes in one request's branches.
    /// </summary>
    private bool BranchReadsAll => _userContextAccessor?.Current?.Branches?.ReadsAll == true;

    /// <summary>
    /// The branches the ambient principal may read (Pass 59). Empty with no principal or no computed
    /// access, so a branch-scoped query then returns no rows: unknown is treated as no access.
    /// </summary>
    private int[] BranchReadIds => _userContextAccessor?.Current?.Branches?.ReadIds switch
    {
        null => [],
        int[] array => array,
        { } list => list.ToArray()
    };
#nullable restore

    public DbSet<Tenant> Tenants { get; set; }
    public DbSet<TenantUser> TenantUsers { get; set; }
    public DbSet<AuditTrail> AuditTrails { get; set; }
    public DbSet<Document> Documents { get; set; }

    public DbSet<PicklistSet> PicklistSets { get; set; }
    public DbSet<SecurityPolicy> SecurityPolicies { get; set; }
    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; }

    /// <summary>Idempotency records (Pass 58). Written only by <c>IdempotencyStore</c>, in raw SQL.</summary>
    public DbSet<ProcessedRequest> ProcessedRequests { get; set; }

    /// <summary>Gap-free document counters (Pass 58). Written only by <c>DocumentNumberAllocator</c>, in raw SQL.</summary>
    public DbSet<DocumentSequence> DocumentSequences { get; set; }

    /// <summary>Branches (Pass 59).</summary>
    public DbSet<Branch> Branches { get; set; }

    /// <summary>User-to-branch assignments (Pass 59).</summary>
    public DbSet<UserBranch> UserBranches { get; set; }

    /// <summary>Approval limits by role and document type (Pass 60).</summary>
    public DbSet<ApprovalLimit> ApprovalLimits { get; set; }

    /// <summary>
    /// Runs the Pass 59 data rules first, then saves, then refreshes any <c>Saved&lt;TId&gt;</c> results.
    /// The rules run before every save interceptor - see <see cref="Rules.DataRules"/>.
    /// </summary>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        DataRules.BeforeSave(this, _userContextAccessor);
        var written = base.SaveChanges(acceptAllChangesOnSuccess);
        DataRules.AfterSave(this);
        return written;
    }

    /// <inheritdoc cref="SaveChanges(bool)"/>
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        DataRules.BeforeSave(this, _userContextAccessor);
        var written = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        DataRules.AfterSave(this);
        return written;
    }

    /// <summary>
    /// True while this context belongs to an active unit of work (Pass 58). <c>Dispose</c> then does
    /// nothing: every handler in the unit of work gets this one context from
    /// <c>IApplicationDbContextFactory</c> and disposes it with <c>await using</c>, and only the unit of
    /// work, when it ends, may actually dispose it.
    /// </summary>
    internal bool LeasedToUnitOfWork { get; set; }

    public override ValueTask DisposeAsync() =>
        LeasedToUnitOfWork ? ValueTask.CompletedTask : base.DisposeAsync();

    public override void Dispose()
    {
        if (LeasedToUnitOfWork) return;
        base.Dispose();
    }

    /// <summary>
    /// The business model's entity configurations. Anything outside this namespace - today, the
    /// log model under <c>Persistence.Logging.Configurations</c> - is deliberately not part of this
    /// context.
    /// </summary>
    public static readonly string ConfigurationsNamespace = typeof(AuditTrailConfiguration).Namespace!;

    /// <summary>The log model's configurations' namespace, which this context never applies (Pass 60).</summary>
    public static readonly string LogConfigurationsNamespace =
        typeof(CleanArchitecture.Blazor.Infrastructure.Persistence.Logging.Configurations.SystemLogConfiguration).Namespace!;

    /// <summary>True for a configuration of the log model, in its namespace or below it.</summary>
    public static bool IsLogConfiguration(Type configuration) =>
        configuration.Namespace is { } ns
        && (ns == LogConfigurationsNamespace || ns.StartsWith(LogConfigurationsNamespace + ".", StringComparison.Ordinal));

    /// <summary>The PostgreSQL partial unique index that allows one default branch per user (Pass 60b).</summary>
    public const string UserBranchDefaultIndex = "IX_UserBranches_UserId_Default";

    protected override void OnModelCreating(ModelBuilder builder)
    {

        base.OnModelCreating(builder);

        // The predicate is load-bearing. ApplyConfigurationsFromAssembly calls builder.Entity<T>()
        // for every IEntityTypeConfiguration<T> it finds, which ADDS T to the model - so an
        // unfiltered scan of this assembly would re-add SystemLog here however thoroughly its DbSet
        // is removed, and the migration would go on creating a SystemLogs table in the business
        // database.
        //
        // PASS 60: an EXCLUSION, where it used to be an equality on this namespace. Module entities'
        // configurations live under Infrastructure/Modules/<M>/Configurations and must be applied, and a
        // module entity has no DbSet (amendment C3), so its configuration is the only thing that brings it
        // into the model. An equality silently dropped them. ConfigurationScanTests asserts that every
        // configuration in the assembly is applied here except the log model's.
        builder.ApplyConfigurationsFromAssembly(
            Assembly.GetExecutingAssembly(),
            t => !IsLogConfiguration(t));

#if (UsePostgreSql)
        // Pass 60b (decision 11): at most one default branch per user, in the database. PostgreSQL only: a
        // boolean filter has no spelling all three providers accept (BranchConfiguration), and PostgreSQL is
        // the only target. "At least one" is SetUserBranchesCommand's rule. Named, so it never merges with an
        // index EF would otherwise create on UserId.
        if (Database.IsNpgsql())
        {
            builder.Entity<UserBranch>()
                .HasIndex([nameof(UserBranch.UserId)], UserBranchDefaultIndex)
                .IsUnique()
                .HasFilter("\"IsDefault\"");
        }
#endif

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

        // PASS 59. The branch filter, by marker, alongside the tenant filter and under its own name so
        // the two compose; DataModelGuard fails startup if a marked entity ends up without it.
        ApplyBranchFilters(builder);

        // PASS 59. Every decimal's column type comes from its declared kind ([Money], [Quantity],
        // [UnitCost], [Rate]); an undeclared decimal fails DataModelGuard at startup.
        DataModelConventions.ApplyDecimalKinds(builder);

        // PASS 59. IVersioned maps Version to PostgreSQL's xmin as a row version. On any other provider
        // the property is not mapped, and the helpers that use it throw NotSupportedException.
        DataModelConventions.ApplyVersioning(builder, Database.ProviderName);

        // LAST, and after ApplyConfigurationsFromAssembly: the GX naming standard yields to an
        // explicit ToTable, so the configurations have to have been applied before it runs. It maps
        // every IBusinessEntity - i.e. everything deriving from BaseEntity - to "TBL_UPPER_SNAKE" in
        // its module's schema (Pass 60: [Module<T>], e.g. inv."TBL_STOCK_ITEM"), and leaves this
        // template's own tables (Identity, Tenants, AuditTrails, Documents, PicklistSets, Branches,
        // DataProtectionKeys, __EFMigrationsHistory) in the default schema under their existing names.
        // See GxNamingConventions; DataModelGuard fails startup for an application entity with no module.
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

    /// <summary>
    /// Registers <see cref="QueryFilters.Branch"/> (Pass 59) on every hierarchy root implementing
    /// <see cref="IMustHaveBranch"/> (<c>ReadsAll || ReadIds.Contains(e.BranchId)</c>) or
    /// <see cref="IBranchVisibility{TSelf}"/> (<c>ReadsAll || TSelf.VisibleTo(e, ReadIds)</c>).
    /// </summary>
    /// <remarks>
    /// Built against <c>this</c>, like <see cref="ApplyTenantFilters"/>, so the read set is bound per query.
    /// A root that implements both is skipped here and refused by <c>DataModelGuard</c>.
    /// </remarks>
    private void ApplyBranchFilters(ModelBuilder builder)
    {
        var self = Expression.Constant(this, typeof(ApplicationDbContext));
        var readsAll = Expression.Property(self, BranchReadsAllProperty);
        var readIds = Expression.Property(self, BranchReadIdsProperty);

        foreach (var entityType in builder.Model.GetEntityTypes().ToList())
        {
            if (entityType.IsOwned() || entityType.BaseType is not null) continue;

            var clr = entityType.ClrType;
            var mustHave = typeof(IMustHaveBranch).IsAssignableFrom(clr);
            var visibility = DataModelConventions.BranchVisibilityRuleOf(clr);
            if (mustHave == (visibility is not null)) continue; // neither, or both (the guard refuses both)

            var e = Expression.Parameter(clr, "e");
            Expression rule;
            if (mustHave)
            {
                var own = clr.GetProperty(nameof(IMustHaveBranch.BranchId), BindingFlags.Instance | BindingFlags.Public);
                var branchId = own is not null
                    ? Expression.Property(e, own)
                    : Expression.Property(Expression.Convert(e, typeof(IMustHaveBranch)), nameof(IMustHaveBranch.BranchId));
                rule = Expression.Call(ContainsInt, readIds, branchId);
            }
            else
            {
                rule = ReplacingExpressionVisitor.Replace(
                    visibility!.Parameters[0], e,
                    ReplacingExpressionVisitor.Replace(visibility.Parameters[1], readIds, visibility.Body));
            }

            builder.Entity(clr).HasQueryFilter(QueryFilters.Branch, Expression.Lambda(Expression.OrElse(readsAll, rule), e));
        }
    }

    private static readonly MethodInfo ContainsInt = typeof(Enumerable).GetMethods()
        .Single(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2)
        .MakeGenericMethod(typeof(int));

    private static readonly PropertyInfo BranchReadsAllProperty =
        typeof(ApplicationDbContext).GetProperty(nameof(BranchReadsAll), BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly PropertyInfo BranchReadIdsProperty =
        typeof(ApplicationDbContext).GetProperty(nameof(BranchReadIds), BindingFlags.Instance | BindingFlags.NonPublic)!;

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
