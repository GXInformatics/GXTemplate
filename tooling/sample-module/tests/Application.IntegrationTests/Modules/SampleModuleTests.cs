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
using CleanArchitecture.Blazor.Application.Modules.Sample.Features.Warehouses;
using CleanArchitecture.Blazor.Domain.Modules.Sample.Entities;
using CleanArchitecture.Blazor.TestSupport;
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
        var created = await SendAsync(new AddEditBranchCommand { Code = TestBranches.NewCode(), Name = TestBranches.NewName("Sample branch") });
        created.Succeeded.Should().BeTrue(created.ErrorMessage);
        return created.Data!.Id;
    }

    [Test]
    public async Task S1_TheModulesTables_AreInCore_PluralWithSnakeCaseColumns_AndIntegerKeys()
    {
        // Pass 60c (plan §3.8): until 1.7.1 the bin table was smp."TBL_SAMPLE_BIN".
        await using var db = (ApplicationDbContext)CreateDbContext();
        (await db.Database.SqlQueryRaw<string>(
                "SELECT table_schema || '.' || table_name AS \"Value\" FROM information_schema.tables " +
                "WHERE table_name IN ('TBL_SAMPLE_BINS', 'TBL_WAREHOUSES', 'TBL_LK_WAREHOUSE_TYPES', 'TBL_WAREHOUSE_MOVEMENTS') ORDER BY 1")
            .ToListAsync())
            .Should().Equal("core.TBL_LK_WAREHOUSE_TYPES", "core.TBL_SAMPLE_BINS", "core.TBL_WAREHOUSES", "core.TBL_WAREHOUSE_MOVEMENTS");

        (await db.Database.SqlQueryRaw<string>(
                "SELECT column_name || ':' || data_type AS \"Value\" FROM information_schema.columns " +
                "WHERE table_schema = 'core' AND table_name = 'TBL_WAREHOUSES' ORDER BY 1")
            .ToListAsync())
            .Should().Contain(["id:integer", "branch_id:integer", "warehouse_type_id:integer", "status:integer", "created_by_id:character varying"]);
        (await db.Database.SqlQueryRaw<string>(
                "SELECT column_name || ':' || data_type AS \"Value\" FROM information_schema.columns " +
                "WHERE table_schema = 'core' AND table_name = 'TBL_WAREHOUSE_MOVEMENTS' AND column_name = 'id'")
            .ToListAsync())
            .Should().Equal("id:bigint");
        (await db.Database.SqlQueryRaw<string>(
                "SELECT conname AS \"Value\" FROM pg_constraint k JOIN pg_class c ON c.oid = k.conrelid " +
                "JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'core' AND c.relname = 'TBL_WAREHOUSES' ORDER BY 1")
            .ToListAsync())
            .Should().Equal("ck_tbl_warehouses_code_upper", "fk_tbl_warehouses_branches_branch_id",
                "fk_tbl_warehouses_tbl_lk_warehouse_types_warehouse_type_id", "pk_tbl_warehouses"); // ux_tbl_warehouses_branch_code is an index, not a constraint
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

    // ---- Pass 60c (plan §3.8): the GX conventions in a generated project -------------------------

    [Test]
    public async Task S10_AWarehouseIsCreated_AndItsOpeningMovementPointsAtItsSavedId()
    {
        var branch = await BranchAsync();
        var created = await SendAsync(new CreateWarehouseCommand { BranchId = branch, Code = "MAIN", Name = "Main store" });
        created.Succeeded.Should().BeTrue(created.ErrorMessage);
        created.Data.Should().BeGreaterThan(0);

        var warehouse = (await SendAsync(new GetWarehousesQuery())).Single(w => w.Id == created.Data);
        warehouse.Movements.Should().Be(1, "WarehouseCreated read the id after the save, so the handler's row points at it");
        warehouse.Type.Should().Be("Main", "the lookup row the migration seeded");
        warehouse.Status.Should().Be(WarehouseStatus.Open);
    }

    [Test]
    public async Task S11_TheLookupIsSeededWithItsIds_AndItsSequenceContinuesAfterThem()
    {
        await using var db = (ApplicationDbContext)CreateDbContext();
        (await db.Set<WarehouseType>().AsNoTracking().OrderBy(t => t.Id).Select(t => t.Id + "=" + t.Name + "/" + t.Slug).ToListAsync())
            .Should().Equal("1=Main/main", "2=In transit/in-transit");

        db.Set<WarehouseType>().Add(new WarehouseType { Name = "Quarantine" });
        await db.SaveChangesAsync();
        (await db.Set<WarehouseType>().AsNoTracking().MaxAsync(t => t.Id)).Should().Be(3, "the seed reset the identity sequence past its ids");
    }

    [Test]
    public async Task S12_TheModulesBranchRule_RefusesACodeBelowTheFloor_AndTheTemplatesTestsPassUnderIt()
    {
        // The whole template suite runs in this project under SampleBranchRules and a unique Branches.Name index (T9).
        await FluentActions.Awaiting(() => SendAsync(new AddEditBranchCommand { Code = "e3f9a1c2b", Name = TestBranches.NewName("Lower") }))
            .Should().ThrowAsync<ValidationException>().WithMessage("*" + SampleBranchRules.Message + "*");
        (await SendAsync(new AddEditBranchCommand { Code = TestBranches.NewCode(), Name = TestBranches.NewName("Floor") })).Succeeded.Should().BeTrue();
    }
}
