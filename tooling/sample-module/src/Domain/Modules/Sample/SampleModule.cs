using CleanArchitecture.Blazor.Domain.Common.Modules;

namespace CleanArchitecture.Blazor.Domain.Modules.Sample;

/// <summary>The smoke test's sample module (pass 60b): proves a generated project with a module stays green.</summary>
public sealed class SampleModule : ModuleDefinition
{
    public override string Name => "Sample";
    public override string Schema => "smp";
    public override int Order => 10;
}
