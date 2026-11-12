// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Text.RegularExpressions;
using CleanArchitecture.Blazor.Domain.Common.Entities;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CleanArchitecture.Blazor.Infrastructure.Persistence.Extensions;

/// <summary>
/// The GX database naming standard for TABLES, applied as one convention loop rather than as a
/// <c>[Table]</c> attribute per entity. Columns and constraint names are <see cref="GxSnakeCaseConvention"/>'s.
/// </summary>
/// <remarks>
/// <list type="table">
/// <item><term>Schema</term><description><c>core</c>, for every business table, whatever its module (GX convention
/// §1.1; Pass 60 put each module in its own schema, Pass 60c restored <c>core</c>)</description></item>
/// <item><term>Tables</term><description><c>TBL_</c> + the PLURAL entity name in upper snake case (Pass 60c,
/// owner decision D3) - <c>core."TBL_STOCK_MOVEMENTS"</c></description></item>
/// <item><term>Lookups</term><description><c>TBL_LK_</c> + the same - <c>core."TBL_LK_ADJUSTMENT_REASONS"</c></description></item>
/// <item><term>Columns</term><description>snake_case on business tables, by <see cref="GxSnakeCaseConvention"/>;
/// never by the EFCore.NamingConventions plug-in, which would also rewrite Identity, the template's tables and
/// <c>__EFMigrationsHistory</c> (see <c>DependencyInjection.UseDatabase</c>).</description></item>
/// </list>
/// <para>
/// Membership is decided by <see cref="IBusinessEntity"/>, which <see cref="BaseEntity"/> carries -
/// so an entity joins the convention by deriving from the template's base, and the template's own
/// infrastructure tables, which do not derive from it (or which pin their name explicitly), stay
/// where they are in the default schema. A module (<c>[Module&lt;T&gt;]</c>) plays no part in naming.
/// </para>
/// </remarks>
public static class GxNamingConventions
{
    /// <summary>The schema every business table is mapped into (GX convention §1.1).</summary>
    public const string BusinessSchema = "core";

    /// <summary>Prefix for a business table.</summary>
    public const string TablePrefix = "TBL_";

    /// <summary>Prefix for a lookup - see <see cref="ILookupEntity"/>.</summary>
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
            // wins over the convention, and gates the schema too: ToTable("Documents") stays in the
            // default schema, and ToTable(name, "reporting") in "reporting" (Pass 60c, owner decision D1,
            // as before Pass 60). DataModelGuard refuses a MODULE entity whose table ends up in the default
            // schema (decision Q-B): public holds Identity and the template's own tables only.
            if (IsNamedByHand(entityType))
            {
                continue;
            }

            entityType.SetSchema(BusinessSchema);
            entityType.SetTableName(TableNameFor(clr));
        }

        // Second pass: many-to-many JOIN tables. EF creates them as shared-type property-bag entities
        // ("SampleCourseSampleStudent") with no CLR type to test, so the first pass never saw them and
        // they would land in the default schema - a project's own data outside core. A join is named when
        // at least one side it links lives in core: TBL_ + the upper-snake of EF's join name, its last
        // word pluralised (Pass 60c, decision Q-C): TBL_SAMPLE_COURSE_SAMPLE_STUDENTS.
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
            join.SetTableName(TablePrefix + ToUpperSnake(Pluralise(join.ShortName())));
        }

        return modelBuilder;
    }

    /// <summary>
    /// The convention's table name for <paramref name="clr"/>: <c>TBL_</c> or <c>TBL_LK_</c> + the upper snake case
    /// of its plural - <see cref="PluralNameAttribute"/> when the class carries one, <see cref="Pluralise"/> otherwise.
    /// </summary>
    public static string TableNameFor(Type clr)
    {
        var prefix = typeof(ILookupEntity).IsAssignableFrom(clr) ? LookupTablePrefix : TablePrefix;
        string plural;
        if (clr.GetCustomAttribute<PluralNameAttribute>(inherit: false) is { } given)
        {
            if (!PascalIdentifier.IsMatch(given.Plural))
                throw new InvalidOperationException(
                    $"{clr.Name}: [PluralName(\"{given.Plural}\")] is not a PascalCase identifier. Give the plural of the whole " +
                    $"class name, for example [PluralName(\"{clr.Name}s\")] (README \"Database naming\").");
            plural = given.Plural;
        }
        else
        {
            plural = Pluralise(clr.Name);
        }

        return prefix + ToUpperSnake(plural);
    }

    private static readonly Regex PascalIdentifier = new("^[A-Z][A-Za-z0-9]*$", RegexOptions.Compiled);

    // ---- Pluralisation (Pass 60c, owner decision D3) --------------------------------------------------

    // The last word of a PascalCase name: an acronym run not followed by a lower-case letter, a capitalised word
    // with its digits, or a leading lower-case run.
    private static readonly Regex Words = new("[A-Z]+(?![a-z])|[A-Z][a-z0-9]*|[a-z0-9]+", RegexOptions.Compiled);

    /// <summary>Irregular plurals: the template's fixed list. Anything else irregular is a <see cref="PluralNameAttribute"/>.</summary>
    public static readonly IReadOnlyDictionary<string, string> IrregularPlurals = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["person"] = "people", ["child"] = "children", ["man"] = "men", ["woman"] = "women",
        ["criterion"] = "criteria", ["datum"] = "data", ["medium"] = "media", ["index"] = "indices",
        ["matrix"] = "matrices", ["vertex"] = "vertices", ["leaf"] = "leaves", ["shelf"] = "shelves",
        ["half"] = "halves", ["knife"] = "knives", ["life"] = "lives", ["wife"] = "wives",
        ["foot"] = "feet", ["tooth"] = "teeth", ["goose"] = "geese", ["mouse"] = "mice", ["ox"] = "oxen",
        ["quiz"] = "quizzes",
    };

    /// <summary>Words left unchanged: uncountable, or already the plural of an irregular word.</summary>
    public static readonly IReadOnlySet<string> UnchangedWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "equipment", "information", "data", "series", "species", "news", "staff", "personnel", "feedback",
        "software", "metadata", "sheep", "deer",
        "people", "children", "men", "women", "criteria", "media", "indices", "matrices", "vertices",
        "leaves", "shelves", "halves", "knives", "lives", "wives", "feet", "teeth", "geese", "mice", "oxen", "quizzes",
    };

    /// <summary>
    /// The plural of a PascalCase entity name. Only the LAST word changes: <c>StockMovement</c> →
    /// <c>StockMovements</c>, and an already plural <c>StockMovements</c> stays as it is.
    /// </summary>
    /// <remarks>
    /// The rules, in order (README "Database naming"; every case is pinned by <c>PluralisationTests</c>):
    /// <list type="number">
    /// <item><description>an irregular word from <see cref="IrregularPlurals"/>;</description></item>
    /// <item><description>an uncountable or already irregular-plural word from <see cref="UnchangedWords"/>: unchanged;</description></item>
    /// <item><description>a word ending in a digit cannot be pluralised: it throws, asking for <see cref="PluralNameAttribute"/>;</description></item>
    /// <item><description>an acronym (all capitals): unchanged if it ends in S, otherwise + S (<c>VAT</c> → <c>VATS</c>; a lower-case s would split the acronym in upper snake case);</description></item>
    /// <item><description>ends in s, but not ss / us / is / as / os: already plural, unchanged;</description></item>
    /// <item><description>ends in sis: → ses (<c>Analysis</c> → <c>Analyses</c>);</description></item>
    /// <item><description>ends in s, x, z, ch or sh: + es (<c>Status</c> → <c>Statuses</c>, <c>Box</c> → <c>Boxes</c>);</description></item>
    /// <item><description>a consonant then y: → ies (<c>Category</c> → <c>Categories</c>); a vowel then y: + s;</description></item>
    /// <item><description>otherwise + s.</description></item>
    /// </list>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The name ends in a digit.</exception>
    public static string Pluralise(string name)
    {
        var matches = Words.Matches(name);
        if (matches.Count == 0) throw new InvalidOperationException($"'{name}' has no word to pluralise.");
        var last = matches[^1];
        var head = name[..last.Index];
        var word = last.Value;
        var lower = word.ToLowerInvariant();

        string plural;
        if (IrregularPlurals.TryGetValue(lower, out var irregular))
        {
            plural = char.IsUpper(word[0]) ? char.ToUpperInvariant(irregular[0]) + irregular[1..] : irregular;
        }
        else if (UnchangedWords.Contains(lower))
        {
            plural = word;
        }
        else if (char.IsDigit(word[^1]))
        {
            throw new InvalidOperationException(
                $"'{name}' ends in a digit, so its plural cannot be worked out. Give it one with " +
                $"[PluralName(\"…\")] (README \"Database naming\").");
        }
        else if (word.Length > 1 && word.All(char.IsUpper))
        {
            plural = word.EndsWith('S') ? word : word + "S";
        }
        else if (lower.EndsWith('s') && !(lower.EndsWith("ss") || lower.EndsWith("us") || lower.EndsWith("is")
                                          || lower.EndsWith("as") || lower.EndsWith("os")))
        {
            plural = word;
        }
        else if (lower.EndsWith("sis"))
        {
            plural = word[..^2] + "es";
        }
        else if (lower.EndsWith('s') || lower.EndsWith('x') || lower.EndsWith('z') || lower.EndsWith("ch") || lower.EndsWith("sh"))
        {
            plural = word + "es";
        }
        else if (lower.EndsWith('y') && lower.Length > 1 && !"aeiou".Contains(lower[^2]))
        {
            plural = word[..^1] + "ies";
        }
        else
        {
            plural = word + "s";
        }

        return head + plural;
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
    /// <c>StockMovements</c> → <c>STOCK_MOVEMENTS</c>; <c>UomConversions</c> → <c>UOM_CONVERSIONS</c>;
    /// <c>IMSSettings</c> → <c>IMS_SETTINGS</c>.
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
