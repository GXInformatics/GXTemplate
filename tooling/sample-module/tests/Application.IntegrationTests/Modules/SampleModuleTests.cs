using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Application.Features.Branches;
using CleanArchitecture.Blazor.Application.Modules.Sample.Contracts;
using CleanArchitecture.Blazor.Application.Modules.Sample.EventHandlers;
using CleanArchitecture.Blazor.Application.Modules.Sample.Features.SampleBins;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Modules.Sample;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using FluentAssertions;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using static CleanArchitecture.Blazor.Application.IntegrationTests.Testing;

namespace CleanArchitecture.Blazor.Application.IntegrationTests.Modules;

/// <summary>
/// Pass 60b plan §5 rows S1 to S6, S8 and S9: the smoke test's sample module, in a generated project. Shipped only in
/// tooling/sample-module; the smoke copies it in, adds its migration and runs every suite.
/// </summary>
public class SampleModuleTests : TestBase
{
    private static async Task<int> BranchAsync()
    {
        var created = await SendAsync(new AddEditBranchCommand { Code = "S" + Guid.NewGuid().ToString("N")[..6], Name = "Sample branch" });
        created.Succeeded.Should().BeTrue(created.ErrorMessage);
        return created.Data!.Id;
    }

    [Test]
    public async Task S1_TheBinTable_IsInTheModulesSchema()
    {
        await using var db = (ApplicationDbContext)CreateDbContext();
        var tables = await db.Database.SqlQueryRaw<string>(
                "SELECT table_schema || '.' || table_name AS \"Value\" FROM information_schema.tables WHERE table_name = 'TBL_SAMPLE_BIN'")
            .ToListAsync();
        tables.Should().Equal("smp.TBL_SAMPLE_BIN");
    }

    [Test]
    public async Task S2_ABinIsCreatedInItsBranch_Announced_AndAUserOfAnotherBranchCannotReadIt()
    {
        var (first, second) = (await BranchAsync(), await BranchAsync());
        var created = await SendAsync(new CreateSampleBinCommand { BranchId = first, Name = "Aisle 1" });
        created.Succeeded.Should().BeTrue(created.ErrorMessage);

        var mine = (await SendAsync(new GetSampleBinsQuery())).Single(b => b.Id == created.Data);
        mine.Announced.Should().BeTrue("the module's own posting handler ran in the transaction");

        // A user of the second branch only.
        string reader;
        using (var scope = CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { UserName = "bins-" + Guid.NewGuid().ToString("N")[..6], Email = "b@example.com", IsActive = true };
            (await userManager.CreateAsync(user, "Password123!")).Succeeded.Should().BeTrue();
            await userManager.AddClaimAsync(user, new Claim(ApplicationClaimTypes.Permission, Permissions.SampleBins.View));
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.UserBranches.Add(new UserBranch { UserId = user.Id, BranchId = second, IsDefault = true });
            await db.SaveChangesAsync();
            reader = user.Id;
        }
        UseUser(reader);
        var seen = await SendAsync(new GetSampleBinsQuery());
        seen.Should().NotContain(b => b.Id == created.Data);
        seen.Should().OnlyContain(b => b.BranchId == second);
    }

    [Test]
    public void S3_TheContractsInterface_ResolvesToTheInfrastructureImplementation()
    {
        using var scope = CreateScope();
        scope.ServiceProvider.GetRequiredService<ISampleBinLookup>().Should().BeOfType<SampleBinLookup>();
    }

    [Test]
    public async Task S4_S5_ProvisioningRunsTheModulesSeeder_AndCreatesItsCatalogueRole()
    {
        using (var scope = CreateScope())
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContextInitializer>().ProvisionAsync();

        SampleTenantSeeder.Seeded.Should().NotBeEmpty("the seeder registered through the hook ran");

        using var check = CreateScope();
        var roleManager = check.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        var role = await roleManager.FindByNameAsync(CleanArchitecture.Blazor.Application.Modules.Sample.SampleServices.BinKeeper);
        role.Should().NotBeNull();
        (await roleManager.GetClaimsAsync(role!)).Select(c => c.Value).Should().BeEquivalentTo(
            Permissions.SampleBins.View, Permissions.SampleBins.Create, Permissions.SampleTools.Use);

        // Decided by attribute: the administrator holds View ([AdministratorGrant]) and not Create or Use.
        var admin = await roleManager.FindByNameAsync(Roles.Admin);
        var adminClaims = (await roleManager.GetClaimsAsync(admin!)).Select(c => c.Value).ToList();
        adminClaims.Should().Contain(Permissions.SampleBins.View).And.NotContain(Permissions.SampleBins.Create)
            .And.NotContain(Permissions.SampleTools.Use);
    }

    [Test]
    public async Task S6_CreatingABranch_OpensItsDefaultBin_InTheSameTransaction()
    {
        var branch = await BranchAsync();
        using var scope = CreateScope();
        (await scope.ServiceProvider.GetRequiredService<ISampleBinLookup>().CountAsync(branch, default)).Should().Be(1);
        (await SendAsync(new GetSampleBinsQuery())).Should().Contain(b => b.BranchId == branch && b.Name == OpenBinForNewBranch.DefaultBinName);
    }

    [Test]
    public async Task S8_TheModulesBranchValidator_RefusesAReservedCode()
    {
        await FluentActions.Awaiting(() => SendAsync(new AddEditBranchCommand { Code = "ZZ1", Name = "Reserved" }))
            .Should().ThrowAsync<ValidationException>().WithMessage("*" + ReservedBranchCodeValidator.Message + "*");
    }

    [Test]
    public void S9_TheApplicationLevelPermission_IsDecidedByAttribute_AndNotGrantedToTheAdministrator()
    {
        FluentActions.Invoking(AdministratorPermissionRegistry.AssertNoDivergence).Should().NotThrow();
        AdministratorPermissionRegistry.EffectiveGranted.Should().Contain(Permissions.SampleBins.View)
            .And.NotContain(Permissions.SampleTools.Use);
    }
}
