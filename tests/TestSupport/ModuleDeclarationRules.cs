using System.Reflection;
using CleanArchitecture.Blazor.Domain.Common.Entities;
using CleanArchitecture.Blazor.Domain.Common.Modules;
using Microsoft.EntityFrameworkCore;

namespace CleanArchitecture.Blazor.TestSupport;

/// <summary>Architecture rules 8 and 10 (Pass 60): where module entities are declared and how they are reached.</summary>
public static class ModuleDeclarationRules
{
    /// <summary>
    /// Rule 8: an entity under <c>….Modules.&lt;M&gt;…</c> declares <c>[Module&lt;…&gt;]</c> with the module named
    /// <c>M</c>; an entity outside any module namespace (the template's <c>Domain.Entities</c>) declares none. A
    /// file moved between module folders, or a module entity left in the template's folder, therefore fails.
    /// </summary>
    public static IReadOnlyList<string> MisdeclaredEntities(IEnumerable<Type> types)
    {
        var problems = new List<string>();
        foreach (var type in types.Where(t => t is { IsClass: true, IsAbstract: false } && IsEntity(t)))
        {
            var byNamespace = ModuleBoundaryRule.ModuleOf(type);
            var declared = ModuleDefinition.Of(type);

            if (byNamespace is not null && declared is null)
                problems.Add($"{type.FullName}: is in module {byNamespace}'s namespace but declares no [Module<>].");
            else if (byNamespace is not null && !string.Equals(declared!.Name, byNamespace, StringComparison.Ordinal))
                problems.Add($"{type.FullName}: is in module {byNamespace}'s namespace but declares module {declared.Name}.");
            else if (ModuleBoundaryRule.IsAppLevel(type))
                problems.Add($"{type.FullName}: is application-level code (AppLevel), which holds no entities. " +
                             "An entity belongs to a module: Domain/Modules/<Module>/Entities (pass 60b).");
            else if (byNamespace is null && declared is not null)
                problems.Add($"{type.FullName}: declares module {declared.Name} but is not under Modules.{declared.Name}; " +
                             "module entities live in Domain/Modules/<Module>/, not in the template's folders.");
        }
        return problems;
    }

    /// <summary>
    /// Rule 10 (amendment C3): <paramref name="contextType"/> declares no <c>DbSet&lt;T&gt;</c> property whose <c>T</c>
    /// is a module entity. Module entities are reached through <c>Set&lt;T&gt;()</c>, so a module never edits the
    /// shared context.
    /// </summary>
    public static IReadOnlyList<string> ModuleDbSets(Type contextType)
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var properties = contextType.IsInterface
            ? contextType.GetProperties(flags).Concat(contextType.GetInterfaces().SelectMany(i => i.GetProperties(flags)))
            : contextType.GetProperties(flags);

        return properties
            .Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
            .Select(p => (Property: p, Entity: p.PropertyType.GetGenericArguments()[0]))
            .Where(x => ModuleDefinition.Of(x.Entity) is not null)
            .Select(x => $"{contextType.Name}.{x.Property.Name}: a DbSet of module entity {x.Entity.Name}. Use db.Set<{x.Entity.Name}>() " +
                         "instead; a module never edits the shared context (README, \"Modules and schemas\").")
            .ToList();
    }

    private static bool IsEntity(Type type) =>
        typeof(IBusinessEntity).IsAssignableFrom(type) || type.IsDefined(typeof(ModuleAttribute), inherit: true);
}
