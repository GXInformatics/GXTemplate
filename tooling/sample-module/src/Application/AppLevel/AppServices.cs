using CleanArchitecture.Blazor.Application.Common.Modules;
using Microsoft.Extensions.Configuration;

namespace CleanArchitecture.Blazor.Application.AppLevel;

/// <summary>Amendment E2: the application level's Application registrations.</summary>
public sealed class AppServices : AppLevelServices
{
    public override void Register(IModuleServiceCollection services, IConfiguration configuration) =>
        services.GrantToRole("Bin Keeper", Permissions.SampleTools.Use);
}
