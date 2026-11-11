using System.Reflection;
using System.Text.RegularExpressions;

namespace CleanArchitecture.Blazor.Domain.Common.Modules;

/// <summary>
/// One application module (Pass 60): its name and the PostgreSQL schema its tables live in.
/// </summary>
/// <remarks>
/// An application declares one subclass per module, in <c>Domain/Modules/&lt;Name&gt;/&lt;Name&gt;Module.cs</c>,
/// and marks each of the module's entities <c>[Module&lt;TheModule&gt;]</c>. The schema is written once, here,
/// so a typo cannot create a second schema: <c>[Module&lt;InvtModule&gt;]</c> does not compile. The template
/// itself ships no module. See the README section "Modules and schemas".
/// <code>
/// public sealed class InventoryModule : ModuleDefinition
/// {
///     public override string Name =&gt; "Inventory";
///     public override string Schema =&gt; "inv";
/// }
/// </code>
/// <para>
/// <see cref="Name"/> must equal the namespace segment after <c>Modules</c> (<c>…Modules.Inventory…</c>):
/// the architecture tests place every type in its module by that segment.
/// </para>
/// </remarks>
public abstract class ModuleDefinition
{
    /// <summary>The module's name, equal to its folder and namespace segment, e.g. <c>Inventory</c>.</summary>
    public abstract string Name { get; }

    /// <summary>The PostgreSQL schema of the module's tables: 1 to 16 lower-case ASCII letters, e.g. <c>inv</c>.</summary>
    public abstract string Schema { get; }

    /// <summary>
    /// The module's position when services are registered (Pass 60b, decision 4): lowest first, ties broken by
    /// <see cref="Name"/>. It matters only for services registered more than once, chiefly seeders, which run in
    /// registration order: give a module whose seeded rows another module's seeders read the lower number.
    /// </summary>
    public virtual int Order => 0;

    /// <summary>Schemas a module may not use: the default schema, the catalogue schemas and SQL Server's default.</summary>
    public static readonly IReadOnlySet<string> ReservedSchemas =
        new HashSet<string>(StringComparer.Ordinal) { "public", "information_schema", "dbo", "sys" };

    private static readonly Regex SchemaPattern = new("^[a-z]{1,16}$", RegexOptions.Compiled);

    /// <summary>Why <paramref name="schema"/> cannot be a module schema, or null when it can.</summary>
    public static string? SchemaProblem(string schema)
    {
        if (!SchemaPattern.IsMatch(schema)) return $"'{schema}' is not 1 to 16 lower-case ASCII letters";
        if (ReservedSchemas.Contains(schema))
            return $"'{schema}' is reserved";
        return null;
    }

    /// <summary>
    /// The module <paramref name="clr"/> declares with <c>[Module&lt;T&gt;]</c> (inherited from a base class), or
    /// null when it declares none.
    /// </summary>
    public static ModuleDefinition? Of(Type clr) =>
        clr.GetCustomAttributes<ModuleAttribute>(inherit: true).FirstOrDefault()?.Module;

    public override string ToString() => $"{Name} ({Schema})";
}

/// <summary>The non-generic face of <see cref="ModuleAttribute{TModule}"/>, so the module can be read by reflection.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public abstract class ModuleAttribute : Attribute
{
    public abstract ModuleDefinition Module { get; }
}

/// <summary>
/// Places an entity in a module (Pass 60): its table goes to <c>TModule</c>'s schema.
/// <code>[Module&lt;InventoryModule&gt;] public class StockItem : BaseVersionedEntity&lt;Guid&gt;, IMustHaveBranch { … }</code>
/// </summary>
/// <remarks>
/// An application entity without it fails <c>DataModelGuard</c> at startup. A module entity is reached through
/// <c>IApplicationDbContext.Set&lt;T&gt;()</c>, never through a DbSet property on the shared context (amendment C3).
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public sealed class ModuleAttribute<TModule> : ModuleAttribute where TModule : ModuleDefinition, new()
{
    private static readonly TModule Instance = new();

    public override ModuleDefinition Module => Instance;
}
