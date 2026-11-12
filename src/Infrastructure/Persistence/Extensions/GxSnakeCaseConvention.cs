using System.Globalization;
using System.Text;
using CleanArchitecture.Blazor.Domain.Common.Entities;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Rules;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace CleanArchitecture.Blazor.Infrastructure.Persistence.Extensions;

/// <summary>
/// snake_case column, key, foreign-key and index names on BUSINESS tables only (Pass 60c, owner decision D5;
/// GX convention §4.1, §4.5): <c>created_by_id</c>, <c>pk_tbl_warehouses</c>,
/// <c>fk_tbl_warehouses_branches_branch_id</c>, <c>ix_tbl_warehouses_branch_id</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not the EFCore.NamingConventions plug-in.</b> It rewrites the whole model: Identity, the template's own
/// tables and <c>__EFMigrationsHistory</c> as well, and a history table renamed under EF's feet leaves a database
/// that can be neither migrated nor inspected (README "Database naming"). This convention visits only business
/// entity types - <see cref="IBusinessEntity"/> types that are not in <see cref="TemplateEntities"/>, the types they
/// own, and the many-to-many joins between them - so every template table and the history keep their names.
/// </para>
/// <para>
/// <b>A model-finalizing convention</b>, so it sees the model complete: shadow foreign keys, indexes the
/// conventions created, discriminators. A column, key, foreign key or index named BY HAND (Explicit or data
/// annotation) is left alone; the generated names are EF's own defaults, computed from the snake_case columns, in
/// lower case - exactly what the plug-in produces in MNEFleets.
/// </para>
/// <para>
/// <b>Hand-named constraints (decision Q-E).</b> On a business table a unique index named by hand must start
/// <c>ux_tbl_</c> and a check constraint <c>ck_tbl_</c>; anything else fails the model build, naming each one.
/// </para>
/// </remarks>
public sealed class GxSnakeCaseConvention : IModelFinalizingConvention
{
    /// <summary>The prefix of a unique index named by hand on a business table (decision Q-E).</summary>
    public const string UniqueIndexPrefix = "ux_tbl_";

    /// <summary>The prefix of a check constraint on a business table (decision Q-E).</summary>
    public const string CheckConstraintPrefix = "ck_tbl_";

    public void ProcessModelFinalizing(IConventionModelBuilder modelBuilder, IConventionContext<IConventionModelBuilder> context)
    {
        var business = modelBuilder.Metadata.GetEntityTypes().Where(IsBusiness).ToList();

        // Columns first: the generated key, foreign-key and index names below are computed from them.
        foreach (var entityType in business)
        {
            // The column a property has IN ITS TABLE: an owned type's key shares its owner's key column (table
            // splitting), and the table-specific name says so where the property's own default would not.
            var table = StoreObjectIdentifier.Create(entityType, StoreObjectType.Table);
            foreach (var property in entityType.GetDeclaredProperties())
            {
                if (property.GetColumnNameConfigurationSource() is ConfigurationSource.Explicit or ConfigurationSource.DataAnnotation)
                    continue;
                var current = (table is { } store ? property.GetColumnName(store) : null) ?? property.GetColumnName();
                var snake = ToSnake(current);
                if (snake != current) property.Builder.HasColumnName(snake);
            }
        }

        var problems = new List<string>();
        foreach (var entityType in business)
        {
            foreach (var key in entityType.GetDeclaredKeys())
            {
                if (IsByHand(key.GetNameConfigurationSource())) continue;
                if (key.GetDefaultName() is { } name) key.Builder.HasName(name.ToLowerInvariant());
            }

            foreach (var foreignKey in entityType.GetDeclaredForeignKeys())
            {
                if (IsByHand(foreignKey.GetConstraintNameConfigurationSource())) continue;
                if (foreignKey.GetDefaultName() is { } name) foreignKey.Builder.HasConstraintName(name.ToLowerInvariant());
            }

            foreach (var index in entityType.GetDeclaredIndexes())
            {
                if (IsByHand(index.GetDatabaseNameConfigurationSource()))
                {
                    if (index.IsUnique && index.GetDatabaseName() is { } given && !given.StartsWith(UniqueIndexPrefix, StringComparison.Ordinal))
                        problems.Add($"{entityType.DisplayName()}: the unique index '{given}' is named by hand, so its name must start " +
                                     $"'{UniqueIndexPrefix}' (for example {UniqueIndexPrefix}{Table(entityType)}_…).");
                    continue;
                }
                if (index.GetDefaultDatabaseName() is { } name) index.Builder.HasDatabaseName(name.ToLowerInvariant());
            }

            foreach (var check in entityType.GetDeclaredCheckConstraints())
            {
                var name = check.Name ?? check.ModelName;
                if (!name.StartsWith(CheckConstraintPrefix, StringComparison.Ordinal))
                    problems.Add($"{entityType.DisplayName()}: the check constraint '{name}' must be named '{CheckConstraintPrefix}…' " +
                                 $"(for example {CheckConstraintPrefix}{Table(entityType)}_…).");
            }
        }

        if (problems.Count > 0)
            throw new InvalidOperationException(
                $"GX naming check failed: {problems.Count} constraint name(s) on business tables break the convention (decision Q-E)." +
                Environment.NewLine + string.Join(Environment.NewLine, problems.Select(p => "  - " + p)) + Environment.NewLine +
                "See the README section \"Database naming\".");
    }

    private static string Table(IConventionEntityType entityType) => (entityType.GetTableName() ?? entityType.ShortName()).ToLowerInvariant();

    private static bool IsByHand(ConfigurationSource? source) =>
        source is ConfigurationSource.Explicit or ConfigurationSource.DataAnnotation;

    /// <summary>
    /// A business entity type: an <see cref="IBusinessEntity"/> that is not a template entity, a type owned by one,
    /// or a many-to-many join with a business side.
    /// </summary>
    public static bool IsBusiness(IReadOnlyEntityType entityType)
    {
        var owner = entityType;
        while (owner.IsOwned() && owner.FindOwnership() is { } ownership) owner = ownership.PrincipalEntityType;

        if (owner.IsPropertyBag)
            return owner.GetForeignKeys().Any(fk => !fk.PrincipalEntityType.IsPropertyBag && IsBusiness(fk.PrincipalEntityType));

        var clr = owner.GetRootType().ClrType;
        return typeof(IBusinessEntity).IsAssignableFrom(clr) && !TemplateEntities.Contains(clr);
    }

    /// <summary>
    /// <c>CreatedById</c> → <c>created_by_id</c>; <c>BHOrderId</c> → <c>bh_order_id</c>; <c>Address_Street</c> →
    /// <c>address_street</c>. The EFCore.NamingConventions snake-case rewrite, character for character, so a name
    /// here is the name MNEFleets has (GX convention §4.1).
    /// </summary>
    public static string ToSnake(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;

        var builder = new StringBuilder(name.Length + Math.Min(2, name.Length / 5));
        UnicodeCategory? previous = null;

        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c == '_')
            {
                if (previous is not null) previous = UnicodeCategory.SpaceSeparator;
                continue;
            }

            var category = char.GetUnicodeCategory(c);
            switch (category)
            {
                case UnicodeCategory.UppercaseLetter:
                case UnicodeCategory.TitlecaseLetter:
                    if (previous == UnicodeCategory.SpaceSeparator
                        || previous == UnicodeCategory.LowercaseLetter
                        || (previous != UnicodeCategory.DecimalDigitNumber && previous is not null
                            && i + 1 < name.Length && char.IsLower(name[i + 1])))
                        builder.Append('_');
                    c = char.ToLowerInvariant(c);
                    break;

                case UnicodeCategory.LowercaseLetter:
                case UnicodeCategory.DecimalDigitNumber:
                    if (previous == UnicodeCategory.SpaceSeparator) builder.Append('_');
                    break;

                default:
                    if (previous is not null) previous = UnicodeCategory.SpaceSeparator;
                    continue;
            }

            builder.Append(c);
            previous = category;
        }

        return builder.ToString();
    }
}

/// <summary>
/// Column names for SQL fragments written by hand in a business entity's configuration (Pass 60c):
/// <c>HasFilter($"{GxSql.Column(nameof(Warehouse.IsMain))}")</c>, <c>HasCheckConstraint("ck_tbl_…", $"{GxSql.Column(nameof(X.Qty))} &gt; 0")</c>.
/// </summary>
/// <remarks>
/// The same rewrite <see cref="GxSnakeCaseConvention"/> applies, quoted, so a fragment follows a property rename
/// instead of failing at <c>database update</c>. Not for the template's own tables, whose columns stay PascalCase.
/// </remarks>
public static class GxSql
{
    /// <summary>The quoted snake_case column of a business entity's property, e.g. <c>"is_main"</c>.</summary>
    public static string Column(string propertyName) => "\"" + GxSnakeCaseConvention.ToSnake(propertyName) + "\"";
}
