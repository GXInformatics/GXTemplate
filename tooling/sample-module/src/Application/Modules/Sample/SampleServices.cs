using CleanArchitecture.Blazor.Application.Common.Modules;
using CleanArchitecture.Blazor.Domain.Modules.Sample;
using Microsoft.Extensions.Configuration;

namespace CleanArchitecture.Blazor.Application.Modules.Sample;

/// <summary>The sample module's Application registrations: its role (item 3).</summary>
public sealed class SampleServices : ModuleServices<SampleModule>
{
    public const string BinKeeper = "Bin Keeper";

    public override void Register(IModuleServiceCollection services, IConfiguration configuration) =>
        services.AddApplicationRole(BinKeeper, "Keeps the bins of their branches.",
            Permissions.SampleBins.View, Permissions.SampleBins.Create);
}
