#nullable enable
using System;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Interfaces.MultiTenant;
using CleanArchitecture.Blazor.Domain.Common.Entities;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Configurations;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Services.Identity;
using CleanArchitecture.Blazor.Infrastructure.Services.MultiTenant;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.MultiTenant;

/// <summary>
/// Pass 54: a model in which a tenant-marked entity is not tenant-filtered refuses to start.
/// </summary>
/// <remarks>
/// None of these open a connection: the guard reads the model, and <c>InitialiseAsync</c> runs it
/// before it migrates anything.
/// </remarks>
[TestFixture]
public class TenantFilterGuardTests
{
    private static DbContextOptions<ApplicationDbContext> Options() =>
        new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql("Host=none").Options;

    // ---- the shipped model ---------------------------------------------------------------------

    [Test]
    public void TheApplicationModel_PassesTheGuard()
    {
        using var db = new ApplicationDbContext(Options());

        TenantFilterGuard.FindUnfiltered(db.Model).Should().BeEmpty();
    }

    [Test]
    public void AProjectEntity_PassesTheGuard_BecauseTheMarkerIsTheWholeDecision()
    {
        using var db = new TenantProbeDbContext(Options(), accessor: null);

        TenantFilterGuard.FindUnfiltered(db.Model).Should().BeEmpty();
    }

    // ---- what it refuses -----------------------------------------------------------------------

    /// <summary>A context that maps a marked entity and never filters it.</summary>
    private sealed class UnfilteredContext(DbContextOptions options) : DbContext(options)
    {
        public DbSet<TenantProbe> Probes => Set<TenantProbe>();
    }

    [Test]
    public void AMarkedEntityWithoutTheFilter_IsRefused_ByName()
    {
        using var db = new UnfilteredContext(new DbContextOptionsBuilder<UnfilteredContext>().UseNpgsql("Host=none").Options);

        var check = () => TenantFilterGuard.AssertEveryTenantEntityIsFiltered(db.Model);

        check.Should().Throw<InvalidOperationException>()
            .WithMessage("*TenantProbe*no 'Tenant' query filter*");
    }

    public class ProbeAnimal : BaseEntity
    {
        public string? Name { get; set; }
    }

    public class ProbeTenantDog : ProbeAnimal, IMustHaveTenant
    {
        public string TenantId { get; set; } = null!;
    }

    /// <summary>A marker on a TPH leaf: EF filters roots only, so the walk cannot filter it.</summary>
    private sealed class MarkedLeafContext(DbContextOptions<ApplicationDbContext> options) : ApplicationDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.Entity<ProbeAnimal>();
            builder.Entity<ProbeTenantDog>();
            base.OnModelCreating(builder);
        }
    }

    [Test]
    public void AMarkedTphLeafUnderAnUnmarkedRoot_IsRefused_NamingTheRoot()
    {
        using var db = new MarkedLeafContext(Options());

        var problems = TenantFilterGuard.FindUnfiltered(db.Model);

        problems.Should().ContainSingle().Which.Should().Contain(nameof(ProbeTenantDog))
            .And.Contain($"Put the marker on {nameof(ProbeAnimal)}");
    }

    // ---- at startup ----------------------------------------------------------------------------

    [Test]
    public async Task Initialise_RefusesToStart_OnAModelThatFailsTheGuard()
    {
        // The real startup method over a bad model: it must throw before it migrates anything (the
        // connection string names no server, so reaching MigrateAsync would fail differently).
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(o => o.UseNpgsql("Host=none"));
        services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(new LeafFactory());
        services.AddIdentityCore<ApplicationUser>().AddRoles<ApplicationRole>().AddEntityFrameworkStores<ApplicationDbContext>();
        services.AddSingleton<IApplicationSettings>(new AppConfigurationSettings());
        services.AddSingleton<IUserContextAccessor, UserContextAccessor>();
        services.AddScoped<ITenantSeedRunner, TenantSeedRunner>();
        services.AddScoped<ApplicationDbContextInitializer>();
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var start = () => scope.ServiceProvider.GetRequiredService<ApplicationDbContextInitializer>().InitialiseAsync();

        await start.Should().ThrowAsync<InvalidOperationException>().WithMessage("Tenant isolation check failed*");
    }

    private sealed class LeafFactory : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new MarkedLeafContext(Options());
    }
}
#nullable restore
