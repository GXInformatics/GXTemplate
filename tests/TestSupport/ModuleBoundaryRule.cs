using System.Reflection;
using CleanArchitecture.Blazor.Domain.Common.Modules;
using Microsoft.EntityFrameworkCore;

namespace CleanArchitecture.Blazor.TestSupport;

/// <summary>One module referring to another's internals.</summary>
public sealed record ModuleViolation(string FromModule, string ToModule, TypeReference Reference)
{
    public override string ToString() =>
        $"{FromModule} -> {ToModule}: {Reference.From.FullName} uses {Reference.To.FullName} ({Reference.Where})";
}

/// <summary>
/// Architecture rule 1 (Pass 60): a module's code references another module only through that module's
/// published <c>Contracts</c> namespaces, never its entities, handlers, pages or features.
/// </summary>
/// <remarks>
/// <para>
/// <b>Which module a type is in</b>: the namespace segment after <c>Modules</c>, in every layer
/// (<c>….Domain.Modules.Inventory.Entities</c>, <c>….Server.UI.Modules.Inventory.Pages</c>). A type with no such
/// segment is template code and belongs to no module; references to it are always allowed.
/// </para>
/// <para>
/// <b>Published</b>: a type whose namespace has the segment <c>Contracts</c> right after the module's name
/// (<c>….Modules.Inventory.Contracts…</c>).
/// </para>
/// <para>
/// <b>The one exemption</b>: an entity configuration (<see cref="IEntityTypeConfiguration{TEntity}"/>) may name
/// another module's ENTITY, because that is how a cross-module foreign key without a navigation is declared
/// (<c>HasOne&lt;Part&gt;().WithMany().HasForeignKey(x =&gt; x.PartId)</c>; owner decision 3).
/// </para>
/// </remarks>
public static class ModuleBoundaryRule
{
    public sealed record Result(IReadOnlyList<ModuleViolation> Violations, IReadOnlyList<string> Unresolved);

    public static Result Check(params Assembly[] assemblies)
    {
        var violations = new List<ModuleViolation>();
        var unresolved = new List<string>();
        foreach (var assembly in assemblies.Distinct())
        {
            var scan = TypeReferenceScanner.Scan(assembly);
            unresolved.AddRange(scan.Unresolved);
            violations.AddRange(Violations(scan.References));
        }
        return new Result(violations.Distinct().ToList(), unresolved);
    }

    /// <summary>The rule over one type and its nested types: used by the wrong samples, one case at a time.</summary>
    public static Result Check(Type type)
    {
        var scan = TypeReferenceScanner.Scan(type);
        return new Result(Violations(scan.References).Distinct().ToList(), scan.Unresolved);
    }

    /// <summary>How application-level code is reported, as either side of a violation (Pass 60b, amendment E2).</summary>
    public const string AppLevel = "AppLevel";

    public static IEnumerable<ModuleViolation> Violations(IEnumerable<TypeReference> references)
    {
        foreach (var reference in references)
        {
            var from = ModuleOf(reference.From);
            var to = ModuleOf(reference.To);

            if (from is null)
            {
                // Pass 60b (E2): application-level code may send a module's commands and use its Contracts, nothing else.
                if (IsAppLevel(reference.From) && to is not null && !IsPublished(reference.To) && !IsRequest(reference.To))
                    yield return new ModuleViolation(AppLevel, to, reference);
                continue;
            }

            // Pass 60b (E2): no module references application-level code.
            if (IsAppLevel(reference.To))
            {
                yield return new ModuleViolation(from, AppLevel, reference);
                continue;
            }

            if (to is null || string.Equals(from, to, StringComparison.Ordinal)) continue;
            if (IsPublished(reference.To)) continue;
            if (IsConfiguration(reference.From) && ModuleDefinition.Of(reference.To) is not null) continue;

            yield return new ModuleViolation(from, to, reference);
        }
    }

    /// <summary>
    /// True for application-level code (Pass 60b, amendment E2): a namespace with the segment <c>AppLevel</c> and no
    /// <c>Modules</c> segment.
    /// </summary>
    public static bool IsAppLevel(Type type)
    {
        var segments = TypeReferenceScanner.Outermost(type).Namespace?.Split('.') ?? [];
        return segments.Contains(AppLevel) && !segments.Contains("Modules");
    }

    /// <summary>A Mediator request (a command or query), or a type nested in one.</summary>
    public static bool IsRequest(Type type) => typeof(Mediator.IMessage).IsAssignableFrom(TypeReferenceScanner.Outermost(type));

    /// <summary>The module of <paramref name="type"/> from its namespace, or null for template code.</summary>
    public static string? ModuleOf(Type type)
    {
        var segments = TypeReferenceScanner.Outermost(type).Namespace?.Split('.') ?? [];
        var index = Array.IndexOf(segments, "Modules");
        return index >= 0 && index + 1 < segments.Length ? segments[index + 1] : null;
    }

    /// <summary>True for a type in a module's <c>Contracts</c> namespace.</summary>
    public static bool IsPublished(Type type)
    {
        var segments = TypeReferenceScanner.Outermost(type).Namespace?.Split('.') ?? [];
        var index = Array.IndexOf(segments, "Modules");
        return index >= 0 && index + 2 < segments.Length && segments[index + 2] == "Contracts";
    }

    private static bool IsConfiguration(Type type) =>
        type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEntityTypeConfiguration<>));
}
