using System.Linq.Expressions;
using System.Reflection;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Domain.Common.Entities;
using CleanArchitecture.Blazor.Domain.Common.Precision;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CleanArchitecture.Blazor.Infrastructure.Persistence.Rules;

/// <summary>
/// Startup proof that the model follows the Pass 59 data rules: declared decimal kinds, no binary floating
/// point, sound immutability markers and a branch filter on every branch-scoped entity.
/// </summary>
/// <remarks>
/// The same posture as <see cref="TenantFilterGuard"/>: an omission fails the start, naming every offender,
/// rather than the first user who meets it. <see cref="AssertModel"/> runs first in
/// <c>ApplicationDbContextInitializer.InitialiseAsync</c>, before migrations;
/// <see cref="AssertAppendOnlyTriggersAsync"/> runs after them, because the migrations create the triggers.
/// </remarks>
public static class DataModelGuard
{
    /// <summary>Every problem in <paramref name="model"/>, one line each. Empty when the model is sound.</summary>
    public static IReadOnlyList<string> FindProblems(IReadOnlyModel model)
    {
        var problems = new List<string>();
        foreach (var entityType in model.GetEntityTypes())
        {
            CheckNumbers(entityType, problems);
            CheckVersioning(entityType, problems);
            CheckImmutability(entityType, problems);
            CheckBranches(entityType, problems);
        }
        return problems;
    }

    /// <summary>Throws, naming every offender, unless <see cref="FindProblems"/> is empty.</summary>
    /// <exception cref="InvalidOperationException">The model breaks a Pass 59 data rule.</exception>
    public static void AssertModel(IReadOnlyModel model)
    {
        var problems = FindProblems(model);
        if (problems.Count == 0) return;

        throw new InvalidOperationException(
            $"Data model check failed: {problems.Count} problem(s)." + Environment.NewLine +
            string.Join(Environment.NewLine, problems.Select(p => "  - " + p)) + Environment.NewLine +
            "See the README sections \"Money and quantities\", \"Concurrency\", \"Posted records\" and \"Branches\". " +
            "The application refuses to start rather than store data these rules exist to protect.");
    }

    // ---- Part A ---------------------------------------------------------------------------------

    private static void CheckNumbers(IReadOnlyEntityType entityType, List<string> problems)
    {
        var name = entityType.ClrType.Name;
        foreach (var property in entityType.GetDeclaredProperties())
        {
            var info = property.PropertyInfo;
            if (DataModelConventions.IsDecimal(property.ClrType))
            {
                var kinds = info?.GetCustomAttributes<DecimalKindAttribute>(inherit: true).ToList() ?? [];
                if (kinds.Count == 0)
                {
                    problems.Add($"{name}.{property.Name}: a decimal with no declared kind. Mark it [Money], [Quantity], [UnitCost] or [Rate].");
                    continue;
                }
                if (kinds.Count > 1)
                {
                    problems.Add($"{name}.{property.Name}: declares more than one kind ({string.Join(", ", kinds.Select(k => k.KindName))}).");
                    continue;
                }
                var kind = kinds[0];
                if (property.GetPrecision() != kind.Precision || property.GetScale() != kind.Scale)
                    problems.Add($"{name}.{property.Name}: is {kind.KindName}, numeric({kind.Precision},{kind.Scale}), but is configured as " +
                                 $"numeric({property.GetPrecision()},{property.GetScale()}). Remove the override; the kind decides.");
            }
            else if (DataModelConventions.IsBinaryFloat(property.ClrType)
                     && info?.GetCustomAttribute<ApproximateAttribute>(inherit: true) is null)
            {
                problems.Add($"{name}.{property.Name}: a {property.ClrType.Name} on an entity. Use decimal with a kind; " +
                             "mark it [Approximate] only for genuinely approximate data such as coordinates.");
            }
        }

        foreach (var info in entityType.ClrType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (info.GetCustomAttributes<DecimalKindAttribute>(inherit: true).FirstOrDefault() is { } kind && !DataModelConventions.IsDecimal(info.PropertyType))
                problems.Add($"{name}.{info.Name}: {kind.KindName} is on a {info.PropertyType.Name}; kinds apply to decimal only.");
        }
    }

    // ---- Part B ---------------------------------------------------------------------------------

    private static void CheckVersioning(IReadOnlyEntityType entityType, List<string> problems)
    {
        if (entityType.IsOwned() || entityType.BaseType is null) return;
        var root = entityType.GetRootType();
        if (typeof(IVersioned).IsAssignableFrom(entityType.ClrType) && !typeof(IVersioned).IsAssignableFrom(root.ClrType))
            problems.Add($"{entityType.ClrType.Name}: is IVersioned, but EF versions whole rows of its hierarchy and its root " +
                         $"{root.ClrType.Name} is not. Put IVersioned on {root.ClrType.Name}.");
    }

    // ---- Part C ---------------------------------------------------------------------------------

    private static void CheckImmutability(IReadOnlyEntityType entityType, List<string> problems)
    {
        var clr = entityType.ClrType;
        var name = clr.Name;
        var appendOnly = typeof(IAppendOnly).IsAssignableFrom(clr);
        var postable = typeof(IPostable).IsAssignableFrom(clr);
        var lineOf = LineHeaderTypeOf(clr);

        if ((appendOnly || postable || lineOf is not null) && typeof(ISoftDelete).IsAssignableFrom(clr))
            problems.Add($"{name}: is {(appendOnly ? "IAppendOnly" : postable ? "IPostable" : "IPostableLine")} and also ISoftDelete. " +
                         "A soft delete is an update, which these records refuse; undo them with a reversing entry.");

        if (appendOnly && postable)
            problems.Add($"{name}: is both IAppendOnly and IPostable. A ledger row is never edited; a document is edited until posted. Choose one.");

        if (postable && !typeof(IVersioned).IsAssignableFrom(clr))
            problems.Add($"{name}: is IPostable but not IVersioned. A line edit touches the header so that it conflicts with a concurrent " +
                         "post on the header's version; derive from BaseVersionedEntity<TKey>.");

        if (lineOf is not null && !entityType.GetNavigations().Any(n => n.Name == "Header" && n.TargetEntityType.ClrType == lineOf))
            problems.Add($"{name}: is IPostableLine<{lineOf.Name}> but has no mapped navigation 'Header' to {lineOf.Name}.");

        var key = entityType.FindPrimaryKey()?.Properties.Select(p => p.Name).ToHashSet() ?? [];
        foreach (var info in clr.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (info.GetCustomAttribute<AllowedAfterPostingAttribute>(inherit: true) is null) continue;
            if (!postable && lineOf is null)
                problems.Add($"{name}.{info.Name}: [AllowedAfterPosting] on a type that is neither IPostable nor IPostableLine<>.");
            else if (info.Name == nameof(IPostable.PostedAt))
                problems.Add($"{name}.{info.Name}: PostedAt can never change after posting; remove [AllowedAfterPosting].");
            else if (key.Contains(info.Name))
                problems.Add($"{name}.{info.Name}: a key cannot change; remove [AllowedAfterPosting].");
        }
    }

    internal static Type? LineHeaderTypeOf(Type clr) =>
        clr.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IPostableLine<>))
            ?.GetGenericArguments()[0];

    // ---- Part D ---------------------------------------------------------------------------------

    private static void CheckBranches(IReadOnlyEntityType entityType, List<string> problems)
    {
        if (entityType.IsOwned()) return;
        var clr = entityType.ClrType;
        var name = clr.Name;
        var mustHave = typeof(IMustHaveBranch).IsAssignableFrom(clr);
        var visibility = clr.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IBranchVisibility<>));
        if (!mustHave && !visibility) return;

        if (mustHave && visibility)
            problems.Add($"{name}: is both IMustHaveBranch and IBranchVisibility<>. A row has one branch or states its own rule, not both.");

        if (clr == typeof(Branch) || clr == typeof(UserBranch))
            problems.Add($"{name}: is a branch table itself and must not be branch-scoped.");

        var root = entityType.GetRootType();
        if (root.FindDeclaredQueryFilter(QueryFilters.Branch) is null)
        {
            problems.Add(ReferenceEquals(root, entityType)
                ? $"{name}: is branch-scoped but has no '{QueryFilters.Branch}' query filter; its rows would be readable by every branch."
                : $"{name}: is branch-scoped, but EF filters only hierarchy roots and its root {root.ClrType.Name} has no " +
                  $"'{QueryFilters.Branch}' query filter. Put the marker on {root.ClrType.Name}.");
        }

        if (visibility && DataModelConventions.BranchVisibilityRuleOf(clr) is { } rule)
        {
            var finder = new NavigationFinder(rule.Parameters[0], entityType);
            finder.Visit(rule.Body);
            if (finder.Found is { } navigation)
                problems.Add($"{name}.VisibleTo: uses '{navigation}', which is not one of {name}'s own columns. A branch rule may read only " +
                             "the entity's own columns, so that it never brings a JOIN that would break ForUpdate().");
        }
    }

    private sealed class NavigationFinder(ParameterExpression entity, IReadOnlyEntityType entityType) : ExpressionVisitor
    {
        public string? Found { get; private set; }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression == entity && entityType.FindProperty(node.Member.Name) is null)
                Found ??= node.Member.Name;
            return base.VisitMember(node);
        }
    }

    // ---- Triggers (owner decision 6) ------------------------------------------------------------

    /// <summary>The trigger names <c>CreateAppendOnlyTrigger</c> creates.</summary>
    public const string RowTrigger = "gx_append_only_row";

    /// <inheritdoc cref="RowTrigger"/>
    public const string TruncateTrigger = "gx_append_only_truncate";

    /// <summary>
    /// Fails if any <see cref="IAppendOnly"/> table lacks its enabled row and <c>TRUNCATE</c> triggers.
    /// PostgreSQL only; on other providers it does nothing (the triggers are not built there).
    /// </summary>
    public static async Task AssertAppendOnlyTriggersAsync(DbContext context, CancellationToken cancellationToken = default)
    {
        var missing = await FindTablesMissingTriggersAsync(context, cancellationToken);
        if (missing.Count == 0) return;

        throw new InvalidOperationException(
            $"Append-only check failed: {missing.Count} table(s) have no enabled append-only triggers: {string.Join(", ", missing)}. " +
            "Add migrationBuilder.CreateAppendOnlyTrigger(table, schema) to the migration that creates each one. The database, " +
            "not only the application, must refuse UPDATE, DELETE and TRUNCATE on a ledger.");
    }

    /// <summary>The append-only tables (schema.table) missing an enabled trigger.</summary>
    public static async Task<IReadOnlyList<string>> FindTablesMissingTriggersAsync(DbContext context, CancellationToken cancellationToken = default)
    {
        if (context.Database.ProviderName != Transactions.PostingSql.NpgsqlProvider) return [];

        var tables = context.Model.GetEntityTypes()
            .Where(t => !t.IsOwned() && t.BaseType is null && typeof(IAppendOnly).IsAssignableFrom(t.ClrType) && t.GetTableName() is not null)
            .Select(t => (Schema: t.GetSchema() ?? "public", Table: t.GetTableName()!))
            .Distinct()
            .ToList();
        if (tables.Count == 0) return [];

        var missing = new List<string>();
        foreach (var (schema, table) in tables)
        {
            var count = await context.Database.SqlQuery<int>(
                    $"""
                     SELECT count(*)::int AS "Value" FROM pg_trigger t
                     JOIN pg_class c ON c.oid = t.tgrelid JOIN pg_namespace n ON n.oid = c.relnamespace
                     WHERE n.nspname = {schema} AND c.relname = {table} AND t.tgenabled <> 'D'
                       AND t.tgname IN ({RowTrigger}, {TruncateTrigger})
                     """)
                .SingleAsync(cancellationToken);
            if (count < 2) missing.Add($"{schema}.{table}");
        }
        return missing;
    }
}
