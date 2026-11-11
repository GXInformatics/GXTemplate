#nullable enable
// Pass 60: wrong samples for architecture rules 8 and 10, in this test assembly (rule 1 does not scan it).

using CleanArchitecture.Blazor.ArchitectureSamples.Modules.SampleAlpha.Entities;
using CleanArchitecture.Blazor.Domain.Common.Entities;
using CleanArchitecture.Blazor.Domain.Common.Modules;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests.Architecture.Samples.Modules.SampleAlpha
{
    /// <summary>Rule 8: in SampleAlpha's namespace, but declares SampleBeta.</summary>
    [Module<CleanArchitecture.Blazor.ArchitectureSamples.Modules.SampleBeta.SampleBetaModule>]
    public class MisdeclaredInAlpha : BaseEntity;

    /// <summary>Rule 8: in SampleAlpha's namespace, declares nothing.</summary>
    public class UndeclaredInAlpha : BaseEntity;

    /// <summary>Rule 8: correct.</summary>
    [Module<CleanArchitecture.Blazor.ArchitectureSamples.Modules.SampleAlpha.SampleAlphaModule>]
    public class DeclaredInAlpha : BaseEntity;
}

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests.Architecture.Samples.Entities
{
    /// <summary>Rule 8: a module entity left outside any module folder.</summary>
    [Module<CleanArchitecture.Blazor.ArchitectureSamples.Modules.SampleAlpha.SampleAlphaModule>]
    public class ModuleEntityInTemplateFolder : BaseEntity;
}

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests.Architecture.Samples
{
    /// <summary>Rule 10: a context with a DbSet property of a module entity.</summary>
    public sealed class PlantedDbSetContext(DbContextOptions<ApplicationDbContext> options) : ApplicationDbContext(options)
    {
        public DbSet<SampleAlphaItem> AlphaItems => Set<SampleAlphaItem>();
    }
}
