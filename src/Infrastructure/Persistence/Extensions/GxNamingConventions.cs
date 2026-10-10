// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using CleanArchitecture.Blazor.Domain.Common.Entities;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CleanArchitecture.Blazor.Infrastructure.Persistence.Extensions;

/// <summary>
/// The GX database naming standard, applied as one convention loop rather than as a
/// <c>[Table]</c> attribute per entity.
/// </summary>
/// <remarks>
/// <list type="table">
/// <item><term>Schema</term><description><c>core</c>, for project business models only</description></item>
/// <item><term>Tables</term><description><c>TBL_UPPER_SNAKE</c> - <c>core."TBL_STOCK_MOVEMENT"</c></description></item>
/// <item><term>Lookups</term><description><c>TBL_LK_UPPER_SNAKE</c> - <c>core."TBL_LK_ADJUSTMENT_REASON"</c></description></item>
/// <item><term>Columns</term><description>PascalCase, quoted by the provider - EF's default, with
/// no snake_case plugin. See <c>DependencyInjection.UseDatabase</c> for why that plugin must not
/// come near this context.</description></item>
/// </list>
/// <para>
/// Membership is decided by <see cref="IBusinessEntity"/>, which <see cref="BaseEntity"/> carries -
/// so an entity joins the convention by deriving from the template's base, and the template's own
/// infrastructure tables, which do not derive from it (or which pin their name explicitly), stay
/// where they are in the default schema.
/// </para>
/// </remarks>
public static class GxNamingConventions
{
    /// <summary>The schema every project business model is mapped into.</summary>
    public const string BusinessSchema = "core";

    /// <summary>Prefix for a business table.</summary>
    public const string TablePrefix = "TBL_";

    /// <summary>Prefix for a pure code/description lookup - see <see cref="ILookupEntity"/>.</summary>
    public const string LookupTablePrefix = "TBL_LK_";

    /// <summary>
    /// Applies the standard. Call at the END of <c>OnModelCreating</c>, after
    /// <c>ApplyConfigurationsFromAssembly</c>, so that an explicit <c>ToTable</c> in a configuration
    /// is already recorded and can win.
    /// </summary>
    public static ModelBuilder ApplyGxTableNaming(this ModelBuilder modelBuilder)
    {
        // Snapshot first: naming does not add or remove entity types, but the loop must not depend
        // on that, and the join pass below reads the names this pass has just set.
        var entityTypes = modelBuilder.Model.GetEntityTypes().ToList();

        foreach (var entityType in entityTypes)
        {
            var clr = entityType.ClrType;

            // A type test, not a namespace string. A namespace test fails SILENTLY the day entities
            // are moved or a generated project renames its root namespace, and the tables quietly
            // revert to public."Items" with nothing to notice. (Join entities are property bags with
            // no CLR type of their own; they are named in the second pass.)
            if (!typeof(IBusinessEntity).IsAssignableFrom(clr) || entityType.IsPropertyBag)
            {
                continue;
            }

            // An owned type shares its owner's table by default (table splitting); naming it would
            // split the value object out into a table of its own. Skipped: the owner carries the name.
            if (entityType.IsOwned())
            {
                continue;
            }

            // Only types that HAVE a table of their own are named - see HasOwnTable for the
            // TPH / TPT / TPC rules. Naming a TPH leaf would turn the hierarchy into TPT, and
            // naming an abstract TPC root would give a type that is never stored a table.
            if (!HasOwnTable(entityType))
            {
                continue;
            }

            // A table name chosen by hand - ToTable(...) in a configuration OR [Table] on the class -
            // wins over the convention, and gates the schema too: a template entity that pins
            // ToTable("Documents") must stay in the default schema, not keep its name and move to
            // core. Until Pass 55 only ToTable was honoured; [Table] (ConfigurationSource
            // DataAnnotation) was renamed into core.
            if (IsNamedByHand(entityType))
            {
                continue;
            }

            var prefix = typeof(ILookupEntity).IsAssignableFrom(clr) ? LookupTablePrefix : TablePrefix;

            entityType.SetSchema(BusinessSchema);
            entityType.SetTableName(prefix + ToUpperSnake(clr.Name));
        }

        // Second pass: many-to-many JOIN tables. EF creates them as shared-type property-bag entities
        // ("SampleCourseSampleStudent") with no CLR type to test, so the first pass never saw them and
        // they landed in the default schema - a project's own data outside core. A join is named when
        // at least one side it links lives in core: TBL_ + the upper-snake of EF's join name.
        foreach (var join in entityTypes.Where(IsJoinEntity))
        {
            if (IsNamedByHand(join))
            {
                continue;
            }

            var linksCore = SkipNavigationsUsing(modelBuilder.Model, join)
                .Any(s => s.DeclaringEntityType.GetRootType().GetSchema() == BusinessSchema
                          || s.TargetEntityType.GetRootType().GetSchema() == BusinessSchema);
            if (!linksCore)
            {
                continue;
            }

            join.SetSchema(BusinessSchema);
            join.SetTableName(TablePrefix + ToUpperSnake(join.ShortName()));
        }

        return modelBuilder;
    }

    /// <summary>
    /// Whether <paramref name="entityType"/> is mapped to a table of its own under its hierarchy's
    /// mapping strategy.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description><b>TPH</b> (EF's default): only the root has a table; every derived type
    /// shares it.</description></item>
    /// <item><description><b>TPT</b>: every type has a table, root and derived alike - each needs its
    /// own <c>TBL_</c> name (until Pass 55 derived types were skipped and kept EF's default name in
    /// the default schema).</description></item>
    /// <item><description><b>TPC</b>: every CONCRETE type has a table holding all of its columns; an
    /// abstract type has none, root or not.</description></item>
    /// </list>
    /// </remarks>
    private static bool HasOwnTable(IMutableEntityType entityType)
    {
        var strategy = entityType.GetRootType().GetMappingStrategy();

        if (strategy == RelationalAnnotationNames.TpcMappingStrategy)
        {
            return !entityType.ClrType.IsAbstract;
        }

        if (strategy == RelationalAnnotationNames.TptMappingStrategy)
        {
            return true;
        }

        // TPH, configured or by default.
        return entityType.BaseType is null;
    }

    /// <summary>
    /// A table name set by hand: <c>ToTable(...)</c> (Explicit) or <c>[Table]</c> (DataAnnotation).
    /// </summary>
    /// <remarks>
    /// The convention's own <c>SetTableName</c> also records Explicit, which is what makes a second
    /// application a no-op rather than <c>TBL_TBL_...</c>.
    /// (In EF Core 10 ConfigurationSource is public API in Metadata - no internal-API escape hatch is
    /// needed to ask "did someone configure this by hand?".)
    /// </remarks>
    private static bool IsNamedByHand(IMutableEntityType entityType) =>
        ((IConventionEntityType)entityType).GetTableNameConfigurationSource()
            is ConfigurationSource.Explicit or ConfigurationSource.DataAnnotation;

    /// <summary>An implicit many-to-many join: a shared-type property bag that skip navigations use.</summary>
    private static bool IsJoinEntity(IMutableEntityType entityType) =>
        entityType.IsPropertyBag
        && entityType.HasSharedClrType
        && SkipNavigationsUsing(entityType.Model, entityType).Any();

    private static IEnumerable<IMutableSkipNavigation> SkipNavigationsUsing(IMutableModel model, IMutableEntityType join) =>
        model.GetEntityTypes()
            .SelectMany(e => e.GetDeclaredSkipNavigations())
            .Where(s => s.JoinEntityType == join);

    /// <summary>
    /// <c>StockMovement</c> → <c>STOCK_MOVEMENT</c>; <c>UomConversion</c> → <c>UOM_CONVERSION</c>;
    /// <c>IMSSetting</c> → <c>IMS_SETTING</c>.
    /// </summary>
    /// <remarks>
    /// Two passes, and the order matters. The first splits an acronym run from the word that
    /// follows it (<c>IMSSetting</c> → <c>IMS_Setting</c>), which a single lower-to-upper rule
    /// cannot see; the second splits an ordinary camel boundary. Run the second alone and
    /// <c>UomConversion</c> is fine but <c>IMSSetting</c> becomes <c>IMSSETTING</c>; run the first
    /// alone and <c>StockMovement</c> is untouched.
    /// <para>
    /// Derived from the CLR type name, never from the current table name, so the convention is
    /// idempotent: applying it twice - or generating a second migration - produces the same name
    /// rather than re-prefixing.
    /// </para>
    /// </remarks>
    public static string ToUpperSnake(string name)
    {
        var s = Regex.Replace(name, "([A-Z]+)([A-Z][a-z])", "$1_$2");
        s = Regex.Replace(s, "([a-z0-9])([A-Z])", "$1_$2");
        return s.ToUpperInvariant();
    }
}
