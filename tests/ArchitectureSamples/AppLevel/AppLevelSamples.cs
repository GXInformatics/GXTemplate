using CleanArchitecture.Blazor.ArchitectureSamples.Modules.SampleAlpha.Contracts;
using CleanArchitecture.Blazor.ArchitectureSamples.Modules.SampleAlpha.Entities;
using CleanArchitecture.Blazor.ArchitectureSamples.Modules.SampleAlpha.Features;
using CleanArchitecture.Blazor.Domain.Common.Entities;

namespace CleanArchitecture.Blazor.ArchitectureSamples.AppLevel;

// Pass 60b, amendment E2: application-level code, which belongs to no module.

/// <summary>Right: sends a module's command and uses its Contracts.</summary>
public sealed class AppLevelSendsAModuleCommand
{
    public CreateAlphaItemCommand Build(string name) => new(name);

    public CreateAlphaItemCommand.Kind DefaultKind => CreateAlphaItemCommand.Kind.Plain;

    public Task<AlphaItemSummary?> FindAsync(IAlphaItemLookup lookup, int id) => lookup.FindAsync(id, CancellationToken.None);
}

/// <summary>Wrong: reads a module's entity, which is internal to the module.</summary>
public sealed class AppLevelUsesAModuleEntity
{
    public string NameOf(SampleAlphaItem item) => item.Name;
}

/// <summary>Something application-level that a module must not reference.</summary>
public static class AppLevelHelper
{
    public static string Banner => "Sample tools";
}

/// <summary>Wrong (rule 8): the application level holds no entities.</summary>
public class AppLevelThing : BaseEntity
{
    public string Name { get; set; } = string.Empty;
}
