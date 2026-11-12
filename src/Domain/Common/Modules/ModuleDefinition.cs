using System.Reflection;

namespace CleanArchitecture.Blazor.Domain.Common.Modules;

/// <summary>
/// One application module (Pass 60): a code boundary. Its tables are in <c>core</c> like every business table
/// (Pass 60c, GX convention §1.1).
/// </summary>
/// <remarks>
/// An application declares one subclass per module, in <c>Domain/Modules/&lt;Name&gt;/&lt;Name&gt;Module.cs</c>,
/// and marks each of the module's entities <c>[Module&lt;TheModule&gt;]</c>. The module decides ownership (startup
/// fails for an application entity without one), forbids navigations to another module's entities, composes the
/// module's services, permissions, roles and menu, and places its code in <c>Modules/&lt;Name&gt;/</c> in every
/// layer. It does <b>not</b> choose a schema: from Pass 60 to 1.7.1 it did, and Pass 60c removed
/// <c>Schema</c>, so a 1.7.x definition that still overrides it no longer compiles. Delete that line.
/// The template itself ships no module. See the README section "Modules".
/// <code>
/// public sealed class InventoryModule : ModuleDefinition
/// {
///     public override string Name =&gt; "Inventory";
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

    /// <summary>
    /// The module's position when services are registered (Pass 60b, decision 4): lowest first, ties broken by
    /// <see cref="Name"/>. It matters only for services registered more than once, chiefly seeders, which run in
    /// registration order: give a module whose seeded rows another module's seeders read the lower number.
    /// </summary>
    public virtual int Order => 0;

    /// <summary>
    /// The module <paramref name="clr"/> declares with <c>[Module&lt;T&gt;]</c> (inherited from a base class), or
    /// null when it declares none.
    /// </summary>
    public static ModuleDefinition? Of(Type clr) =>
        clr.GetCustomAttributes<ModuleAttribute>(inherit: true).FirstOrDefault()?.Module;

    public override string ToString() => Name;
}

/// <summary>The non-generic face of <see cref="ModuleAttribute{TModule}"/>, so the module can be read by reflection.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public abstract class ModuleAttribute : Attribute
{
    public abstract ModuleDefinition Module { get; }
}

/// <summary>
/// Places an entity in a module (Pass 60): the module owns it. Its table is <c>core."TBL_…"</c> (Pass 60c).
/// <code>[Module&lt;InventoryModule&gt;] public class StockItem : BaseVersionedEntity, IMustHaveBranch { … }   // core."TBL_STOCK_ITEMS"</code>
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
