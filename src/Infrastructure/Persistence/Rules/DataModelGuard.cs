using System.Linq.Expressions;
using System.Reflection;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Domain.Common.Entities;
using CleanArchitecture.Blazor.Domain.Common.Modules;
using CleanArchitecture.Blazor.Domain.Common.Precision;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Extensions;
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
            CheckModule(entityType, problems);
            CheckModuleNavigations(entityType, problems);
            CheckApprovable(entityType, problems);
            CheckBusinessSchema(entityType, problems);
            CheckLookup(entityType, problems);
            CheckKey(entityType, problems);
            CheckEnums(entityType, problems);
        }
        CheckModuleDefinitions(model, problems);
        CheckTableNames(model, problems);
        CheckApprovalDocumentTypes(model, problems);
        return problems;
    }

    // ---- Pass 60: approvals ---------------------------------------------------------------------

    private static void CheckApprovable(IReadOnlyEntityType entityType, List<string> problems)
    {
        var clr = entityType.ClrType;
        if (entityType.IsOwned() || !typeof(IApprovable).IsAssignableFrom(clr)) return;
        var name = clr.Name;

        if (!typeof(IVersioned).IsAssignableFrom(clr))
            problems.Add($"{name}: is IApprovable but not IVersioned. An approval must conflict with a concurrent edit of the " +
                         "document; derive from BaseVersionedEntity<long>.");
        if (typeof(ISoftDelete).IsAssignableFrom(clr))
            problems.Add($"{name}: is IApprovable and ISoftDelete. An approved document is never deleted, softly or otherwise.");

        if (!clr.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IApprovable<>) && i.GetGenericArguments()[0] == clr))
            problems.Add($"{name}: implements IApprovable but not IApprovable<{name}>, which declares its approval document type.");

        foreach (var required in new[] { nameof(IApprovable.ApprovalValue), nameof(IApprovable.PreparedById), nameof(IApprovable.ApprovedById), nameof(IApprovable.ApprovedAt) })
        {
            if (entityType.FindProperty(required) is null)
                problems.Add($"{name}.{required}: is not a mapped column. The approval reads and records the STORED values, never computed ones.");
        }

        var value = entityType.FindProperty(nameof(IApprovable.ApprovalValue))?.PropertyInfo;
        if (value is not null && value.GetCustomAttribute<MoneyAttribute>(inherit: true) is null)
            problems.Add($"{name}.ApprovalValue: must be [Money]; approval limits are amounts of money.");
    }

    /// <summary>Two approvable types may not share a document-type code: limits are kept against the code.</summary>
    private static void CheckApprovalDocumentTypes(IReadOnlyModel model, List<string> problems)
    {
        var clashes = model.GetEntityTypes()
            .Where(e => !e.IsOwned() && typeof(IApprovable).IsAssignableFrom(e.ClrType) && !e.ClrType.IsAbstract)
            .Select(e => (Type: e.ClrType, Code: ApprovalDocumentTypeOf(e.ClrType)))
            .GroupBy(x => x.Code, StringComparer.Ordinal)
            .Where(g => g.Count() > 1);
        foreach (var clash in clashes)
            problems.Add($"{string.Join(" and ", clash.Select(x => x.Type.Name))} share the approval document type '{clash.Key}'. " +
                         "Each approvable document has its own code; approval limits are kept against it.");
    }

    internal static string ApprovalDocumentTypeOf(Type clr) =>
        clr.GetProperty("ApprovalDocumentType", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            ?.GetValue(null) as string ?? clr.Name;

    // ---- Pass 60c: the GX conventions -----------------------------------------------------------

    /// <summary>
    /// Decision Q-B: a module entity's table is never in the default schema, which holds Identity and the template's
    /// own tables only (owner decision D2). It gets there only by a hand-named <c>ToTable(name)</c> with no schema.
    /// </summary>
    private static void CheckBusinessSchema(IReadOnlyEntityType entityType, List<string> problems)
    {
        if (entityType.IsOwned() || entityType.IsPropertyBag || entityType.GetTableName() is null) return;
        var clr = entityType.ClrType;
        if (TemplateEntities.Contains(clr) || ModuleDefinition.Of(clr) is null) return;
        if (entityType.GetSchema() is null)
            problems.Add($"{clr.Name}: a module entity whose table '{entityType.GetTableName()}' is in the default schema, which holds " +
                         "Identity and the template's own tables only. Remove the ToTable (the convention names it " +
                         $"core.\"{GxNamingConventions.TableNameFor(clr)}\"), or give the schema explicitly: ToTable(name, \"core\").");
    }

    /// <summary>Owner decisions D4 and Q-A2: a lookup has the fixed columns, an int key, a TBL_LK_ table in core, no decimal.</summary>
    private static void CheckLookup(IReadOnlyEntityType entityType, List<string> problems)
    {
        var clr = entityType.ClrType;
        if (entityType.IsOwned() || !typeof(ILookupEntity).IsAssignableFrom(clr)) return;
        var name = clr.Name;

        var key = entityType.FindPrimaryKey();
        if (key is null || key.Properties.Count != 1 || key.Properties[0].Name != nameof(ILookupEntity.Id) || key.Properties[0].ClrType != typeof(int))
            problems.Add($"{name}: is a lookup, so its key is one int column Id (owner decision D4). Derive from BaseLkEntity.");

        var nameColumn = entityType.FindProperty(nameof(ILookupEntity.Name));
        if (nameColumn is null)
            problems.Add($"{name}.Name: is a lookup's fixed column and is not mapped. Do not Ignore it.");
        else if (nameColumn.IsNullable)
            problems.Add($"{name}.Name: is a lookup's fixed column and must be required.");
        if (entityType.FindProperty(nameof(ILookupEntity.Slug)) is null)
            problems.Add($"{name}.Slug: is a lookup's fixed column and is not mapped. Do not Ignore it.");

        foreach (var property in entityType.GetProperties().Where(p => DataModelConventions.IsDecimal(p.ClrType)))
            problems.Add($"{name}.{property.Name}: a lookup carries no decimal column (decision Q-A2). A rate or an amount makes it " +
                         "master data with rules: derive it from BaseEntity / BaseVersionedEntity, a TBL_ table, and drop ILookupEntity.");

        if (entityType.BaseType is null && entityType.GetTableName() is { } table
            && (entityType.GetSchema() != GxNamingConventions.BusinessSchema || !table.StartsWith(GxNamingConventions.LookupTablePrefix, StringComparison.Ordinal)))
            problems.Add($"{name}: is a lookup, so its table is core.\"{GxNamingConventions.LookupTablePrefix}…\"; it is " +
                         $"{entityType.GetSchema() ?? "(default)"}.\"{table}\". Remove the ToTable.");
    }

    /// <summary>
    /// Owner decisions D6 and Q-I: an application entity's key is one int or long column; documents, lines, ledgers and
    /// approvable headers are long; lookups are int. No Guid, string or composite key.
    /// </summary>
    private static void CheckKey(IReadOnlyEntityType entityType, List<string> problems)
    {
        if (entityType.IsOwned() || entityType.IsPropertyBag || entityType.BaseType is not null) return;
        var clr = entityType.ClrType;
        if (TemplateEntities.Contains(clr) || !typeof(IBusinessEntity).IsAssignableFrom(clr)) return;
        var key = entityType.FindPrimaryKey();
        if (key is null) return; // keyless: a view or a query type, no key to judge
        var name = clr.Name;

        if (key.Properties.Count != 1)
        {
            problems.Add($"{name}: has a composite key ({string.Join(", ", key.Properties.Select(p => p.Name))}). Keys are one integer " +
                         "column (owner decision D6); declare the natural key as a unique index.");
            return;
        }

        var type = key.Properties[0].ClrType;
        if (type != typeof(int) && type != typeof(long))
        {
            problems.Add($"{name}: has a {type.Name} key. Keys are integers (owner decision D6): int for master data and lookups, " +
                         "long for document headers, lines and ledger rows. Never a Guid.");
            return;
        }

        var mustBeLong = typeof(IPostable).IsAssignableFrom(clr) ? "IPostable"
            : LineHeaderTypeOf(clr) is not null ? "IPostableLine<>"
            : typeof(IAppendOnly).IsAssignableFrom(clr) ? "IAppendOnly"
            : typeof(IApprovable).IsAssignableFrom(clr) ? "IApprovable"
            : null;
        if (mustBeLong is not null && type != typeof(long))
            problems.Add($"{name}: is {mustBeLong}, a document or ledger table, so its key is long (owner decision D6). " +
                         "Derive from BaseVersionedEntity<long> or BaseEntity<long>.");
    }

    /// <summary>Owner decision Q-H: every enum a business entity stores is declared [ExplicitValues].</summary>
    private static void CheckEnums(IReadOnlyEntityType entityType, List<string> problems)
    {
        if (!GxSnakeCaseConvention.IsBusiness(entityType)) return;
        foreach (var (path, enumType) in EnumsOf(entityType))
        {
            if (enumType.GetCustomAttribute<ExplicitValuesAttribute>() is null)
                problems.Add($"{entityType.ClrType.Name}.{path}: stores enum {enumType.Name} as an integer, so every member needs an explicit " +
                             $"value and the enum needs [ExplicitValues] (owner decision Q-H): enum {enumType.Name} {{ First = 1, Second = 2 }}.");
        }
    }

    /// <summary>The enum types a business entity stores, with the property path, for the guard and the source test.</summary>
    public static IEnumerable<(string Path, Type Enum)> EnumsOf(IReadOnlyEntityType entityType)
    {
        foreach (var property in entityType.GetDeclaredProperties())
        {
            var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
            if (type.IsEnum) yield return (property.Name, type);
        }
        foreach (var complex in entityType.GetDeclaredComplexProperties())
        {
            foreach (var property in complex.ComplexType.GetProperties())
            {
                var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                if (type.IsEnum) yield return ($"{complex.Name}.{property.Name}", type);
            }
        }
    }

    // ---- Pass 60: modules -----------------------------------------------------------------------

    private static void CheckModule(IReadOnlyEntityType entityType, List<string> problems)
    {
        if (entityType.IsPropertyBag) return; // a many-to-many join: named after its sides
        var clr = entityType.ClrType;
        var name = clr.Name;
        var module = ModuleDefinition.Of(clr);

        if (entityType.IsOwned())
        {
            var owner = entityType.FindOwnership()?.PrincipalEntityType;
            if (module is not null && owner is not null && ModuleDefinition.Of(owner.ClrType)?.GetType() != module.GetType())
                problems.Add($"{name}: is owned by {owner.ClrType.Name} but declares module {module.Name}. An owned type lives in its owner's table; remove [Module<>].");
            return;
        }

        if (TemplateEntities.Contains(clr))
        {
            if (module is not null)
                problems.Add($"{name}: is a template entity and belongs to no module, but declares [Module<{module.GetType().Name}>].");
            return;
        }

        if (module is null)
        {
            problems.Add($"{name}: declares no module. Add [Module<…Module>]: every application entity belongs to one module (pass 60); " +
                         "only the template's own entities (TemplateEntities) live outside a module.");
            return;
        }

        if (entityType.BaseType is { } baseType && ModuleDefinition.Of(entityType.GetRootType().ClrType) is { } rootModule
            && rootModule.GetType() != module.GetType())
            problems.Add($"{name}: declares module {module.Name}, but its hierarchy root {entityType.GetRootType().ClrType.Name} " +
                         $"is in {rootModule.Name}. A hierarchy lives in one module.");
    }

    /// <summary>Architecture rule 2: no navigation crosses a module boundary.</summary>
    private static void CheckModuleNavigations(IReadOnlyEntityType entityType, List<string> problems)
    {
        if (entityType.IsPropertyBag) return;
        var from = ModuleOf(entityType);
        var navigations = entityType.GetDeclaredNavigations().Select(n => (n.Name, Target: n.TargetEntityType))
            .Concat(entityType.GetDeclaredSkipNavigations().Select(s => (s.Name, Target: s.TargetEntityType)));

        foreach (var (navigationName, target) in navigations)
        {
            if (target.IsOwned()) continue; // part of this entity's own table
            var to = ModuleOf(target);
            if (to is null) continue; // a template entity (users, branches): allowed from anywhere
            if (from?.GetType() == to.GetType()) continue;

            problems.Add($"{entityType.ClrType.Name}.{navigationName}: navigates to {target.ClrType.Name} in module {to.Name}" +
                         (from is null ? " from a template entity" : $" from module {from.Name}") +
                         ". No navigation crosses a module boundary: keep the id, and declare the foreign key without a navigation " +
                         "(HasOne<T>().WithMany().HasForeignKey(...)).");
        }
    }

    private static ModuleDefinition? ModuleOf(IReadOnlyEntityType entityType)
    {
        var owner = entityType;
        while (owner.IsOwned() && owner.FindOwnership() is { } ownership) owner = ownership.PrincipalEntityType;
        return ModuleDefinition.Of(owner.GetRootType().ClrType);
    }

    private static void CheckModuleDefinitions(IReadOnlyModel model, List<string> problems)
    {
        var modules = model.GetEntityTypes()
            .Where(e => !e.IsPropertyBag)
            .Select(e => ModuleDefinition.Of(e.ClrType))
            .OfType<ModuleDefinition>()
            .GroupBy(m => m.GetType())
            .Select(g => g.First())
            .ToList();

        // Pass 60c: a module no longer has a schema (every business table is in core), so only its name is checked.
        foreach (var module in modules)
        {
            if (string.IsNullOrWhiteSpace(module.Name))
                problems.Add($"Module {module.GetType().Name}: has no name.");
        }

        foreach (var clash in modules.GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add($"Modules {string.Join(" and ", clash.Select(m => m.GetType().Name))} share the name '{clash.Key}'.");
    }

    /// <summary>
    /// Two tables may not share a name in different schemas: the bulk-write guard matches tables by name in any
    /// schema, and SQLite has no schemas at all. (Pass 60c: every business table is in core, where EF itself refuses
    /// two unrelated entity types on one table; this still catches a table named by hand into another schema.)
    /// </summary>
    private static void CheckTableNames(IReadOnlyModel model, List<string> problems)
    {
        var tables = model.GetEntityTypes()
            .Where(e => e.GetTableName() is not null)
            .Select(e => (Table: e.GetTableName()!, Schema: e.GetSchema() ?? "(default)"))
            .Distinct()
            .GroupBy(t => t.Table, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(t => t.Schema).Distinct().Count() > 1);

        foreach (var clash in tables)
            problems.Add($"Table '{clash.Key}' exists in more than one schema ({string.Join(", ", clash.Select(t => t.Schema))}). " +
                         "Table names must be unique across schemas; rename one of the entities.");
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
            "See the README sections \"Money and quantities\", \"Concurrency\", \"Posted records\", \"Branches\", \"Modules\" and \"Database naming\". " +
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
