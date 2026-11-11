using System;
using System.Linq;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Features.Branches;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using FluentAssertions;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using static CleanArchitecture.Blazor.Application.IntegrationTests.Testing;

namespace CleanArchitecture.Blazor.Application.IntegrationTests.Extensions;

/// <summary>Pass 60b plan §5 row 16: a user with at least one assigned branch has exactly one default (decision 11).</summary>
public class DefaultBranchTests : TestBase
{
    private static async Task<int> BranchAsync(bool active = true)
    {
        var created = await SendAsync(new AddEditBranchCommand { Code = "D" + Guid.NewGuid().ToString("N")[..6], Name = "Default test", IsActive = active });
        created.Succeeded.Should().BeTrue(created.ErrorMessage);
        return created.Data!.Id;
    }

    private static async Task<string> UserAsync()
    {
        using var scope = CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var name = "dflt-" + Guid.NewGuid().ToString("N")[..8];
        var user = new ApplicationUser { UserName = name, Email = name + "@example.com", IsActive = true };
        (await userManager.CreateAsync(user, "Passw0rd!Passw0rd!")).Succeeded.Should().BeTrue();
        return user.Id;
    }

    private static async Task<UserBranch[]> RowsAsync(string userId)
    {
        await using var db = CreateDbContext() as ApplicationDbContext;
        return await db!.UserBranches.AsNoTracking().Where(ub => ub.UserId == userId).OrderBy(ub => ub.BranchId).ToArrayAsync();
    }

    [Test]
    public async Task T16_BranchesWithoutADefault_AreRefused()
    {
        await RunAsAdministratorAsync();
        var (a, b, user) = (await BranchAsync(), await BranchAsync(), await UserAsync());

        var act = () => SendAsync(new SetUserBranchesCommand { UserId = user, BranchIds = [a, b] });
        await act.Should().ThrowAsync<ValidationException>().WithMessage("*" + SetUserBranchesCommandValidator.DefaultRequiredMessage + "*");
        (await RowsAsync(user)).Should().BeEmpty("nothing is saved");
    }

    [Test]
    public async Task T16_ADefaultOutsideTheSet_AndADefaultWithNoBranches_AreRefused()
    {
        await RunAsAdministratorAsync();
        var (a, b, user) = (await BranchAsync(), await BranchAsync(), await UserAsync());

        await FluentActions.Awaiting(() => SendAsync(new SetUserBranchesCommand { UserId = user, BranchIds = [a], DefaultBranchId = b }))
            .Should().ThrowAsync<ValidationException>().WithMessage("*one of the assigned branches*");
        await FluentActions.Awaiting(() => SendAsync(new SetUserBranchesCommand { UserId = user, BranchIds = [], DefaultBranchId = a }))
            .Should().ThrowAsync<ValidationException>().WithMessage("*one of the assigned branches*");
    }

    [Test]
    public async Task T16_OneBranchWithItAsTheDefault_IsAccepted_AndEmptyClearsEverything()
    {
        await RunAsAdministratorAsync();
        var (a, b, user) = (await BranchAsync(), await BranchAsync(), await UserAsync());

        (await SendAsync(new SetUserBranchesCommand { UserId = user, BranchIds = [a], DefaultBranchId = a })).Succeeded.Should().BeTrue();
        (await SendAsync(new SetUserBranchesCommand { UserId = user, BranchIds = [a, b], DefaultBranchId = b })).Succeeded.Should().BeTrue();
        (await RowsAsync(user)).Select(r => (r.BranchId, r.IsDefault)).Should().Equal((a, false), (b, true));

        (await SendAsync(new SetUserBranchesCommand { UserId = user, BranchIds = [] })).Succeeded.Should().BeTrue();
        (await RowsAsync(user)).Should().BeEmpty();
    }

    [Test]
    public async Task T16_ThePostgreSqlIndex_RefusesASecondDefaultWrittenDirectly()
    {
        await RunAsAdministratorAsync();
        var (a, b, user) = (await BranchAsync(), await BranchAsync(), await UserAsync());

        await using var db = CreateDbContext() as ApplicationDbContext;
        db!.UserBranches.AddRange(
            new UserBranch { UserId = user, BranchId = a, IsDefault = true },
            new UserBranch { UserId = user, BranchId = b, IsDefault = true });

        var act = () => db.SaveChangesAsync();
        (await act.Should().ThrowAsync<Exception>()).Which.ToString().Should().Contain(ApplicationDbContext.UserBranchDefaultIndex);
    }

    [Test]
    public async Task T16_TheStartupRepair_PicksTheLowestActiveBranch_AndDoesNothingTheSecondTime()
    {
        await RunAsAdministratorAsync();
        var inactive = await BranchAsync(active: false);
        var (active1, active2) = (await BranchAsync(), await BranchAsync());
        var user = await UserAsync();
        inactive.Should().BeLessThan(active1, "the repair must skip a lower-id inactive branch");

        await using (var db = CreateDbContext() as ApplicationDbContext)
        {
            // A 1.7.0 database: assignments, none of them the default.
            db!.UserBranches.AddRange(
                new UserBranch { UserId = user, BranchId = inactive },
                new UserBranch { UserId = user, BranchId = active2 },
                new UserBranch { UserId = user, BranchId = active1 });
            await db.SaveChangesAsync();
        }

        await ProvisionAsync();
        (await RowsAsync(user)).Single(r => r.IsDefault).BranchId.Should().Be(active1);

        await ProvisionAsync();
        (await RowsAsync(user)).Where(r => r.IsDefault).Select(r => r.BranchId).Should().Equal(active1);
    }

    private static async Task ProvisionAsync()
    {
        using var scope = CreateScope();
        await scope.ServiceProvider.GetRequiredService<ApplicationDbContextInitializer>().ProvisionAsync();
    }
}
