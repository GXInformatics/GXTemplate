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
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var clr = entityType.ClrType;

            // A type test, not a namespace string. A namespace test fails SILENTLY the day entities
            // are moved or a generated project renames its root namespace, and the tables quietly
            // revert to public."Items" with nothing to notice.
            if (!typeof(IBusinessEntity).IsAssignableFrom(clr))
            {
                continue;
            }

            // An owned type shares its owner's table by default (table splitting), and a derived
            // type in a TPH hierarchy shares its root's. Naming either one moves it into a table of
            // its own - turning TPH into TPT, or splitting an owned value object out into a
            // separate table - which is a mapping-strategy change disguised as a rename. Both are
            // skipped: the owner and the hierarchy root carry the name for them.
            if (entityType.IsOwned() || entityType.BaseType is not null)
            {
                continue;
            }

            // An explicit ToTable(...) / [Table] wins over the convention, and gates the schema too:
            // a template entity that pins ToTable("Documents") must stay in the default schema, not
            // keep its name and move to core.
            // (In EF Core 10 ConfigurationSource is public API in Metadata - no internal-API
            //  escape hatch is needed to ask "did someone configure this by hand?".)
            if (((IConventionEntityType)entityType).GetTableNameConfigurationSource()
                == ConfigurationSource.Explicit)
            {
                continue;
            }

            var prefix = typeof(ILookupEntity).IsAssignableFrom(clr) ? LookupTablePrefix : TablePrefix;

            entityType.SetSchema(BusinessSchema);
            entityType.SetTableName(prefix + ToUpperSnake(clr.Name));
        }

        return modelBuilder;
    }

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
