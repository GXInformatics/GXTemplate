using Mediator;

namespace CleanArchitecture.Blazor.ArchitectureSamples.Modules.SampleAlpha.Features;

/// <summary>
/// Pass 60b, amendment E2: a module command that is NOT in Contracts. Another module may not send it (rule 1); the
/// application level may.
/// </summary>
public sealed record CreateAlphaItemCommand(string Name) : IRequest<int>
{
    /// <summary>A nested type of the request: part of what the application level may use.</summary>
    public enum Kind { Plain, Special }
}
