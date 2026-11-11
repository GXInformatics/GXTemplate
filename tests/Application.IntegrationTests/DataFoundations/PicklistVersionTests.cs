using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Concurrency;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.ExceptionHandlers;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Mappings;
using CleanArchitecture.Blazor.Application.Common.Models;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Application.Features.Branches;
using CleanArchitecture.Blazor.Application.Features.PicklistSets.Commands.AddEdit;
using CleanArchitecture.Blazor.Application.IntegrationTests.Transactions;
using CleanArchitecture.Blazor.Application.Pipeline;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Rules;
using CleanArchitecture.Blazor.Infrastructure.Services.Identity;
using FluentAssertions;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NUnit.Framework;
using static CleanArchitecture.Blazor.Application.IntegrationTests.Testing;

namespace CleanArchitecture.Blazor.Application.IntegrationTests.DataFoundations;

/// <summary>
/// Pass 59 plan §5 rows on the template's own entities: the versioned PicklistSet (6, 7, 8, 11, 34),
/// users (20) and the branch read-set refresh (27).
/// </summary>
public class PicklistVersionTests : TestBase
{
    private static AddEditPicklistSetCommand New(string value) => new() { Name = Picklist.Brand, Value = value, Text = value };

    private static AddEditPicklistSetCommand EditOf(int id, uint? version, string text) =>
        new() { Id = id, Version = version, Name = Picklist.Brand, Value = "v-" + id, Text = text };

    private static async Task<Saved<int>> CreateAsync()
    {
        var created = await SendAsync(New("v-" + Guid.NewGuid().ToString("N")[..8]));
        created.Succeeded.Should().BeTrue(created.ErrorMessage);
        // Keep Value stable for EditOf: rename to the id-based value once.
        var value = "v-" + created.Data!.Id;
        var renamed = await SendAsync(new AddEditPicklistSetCommand
            { Id = created.Data.Id, Version = created.Data.Version, Name = Picklist.Brand, Value = value, Text = value });
        renamed.Succeeded.Should().BeTrue(renamed.ErrorMessage);
        return renamed.Data!;
    }

    // ---- 6 and 7 ------------------------------------------------------------------------------

    [Test]
    public async Task T06_AFreshEditWithTheLoadedVersionSucceeds_AndTheVersionChanges()
    {
        var saved = await CreateAsync();
        saved.Version.Should().NotBe(0u);

        var edited = await SendAsync(EditOf(saved.Id, saved.Version, "fresh"));
        edited.Succeeded.Should().BeTrue(edited.ErrorMessage);
        edited.Data!.Version.Should().NotBe(saved.Version);
        (await FindAsync<PicklistSet>(saved.Id))!.Version.Should().Be(edited.Data.Version, "the result holds the stored version");
    }

    [Test]
    public async Task T07_AStaleEditIsRefusedWithTheSpecificMessage_AndTheOtherUsersValuesStay()
    {
        var loaded = await CreateAsync();                                    // both users load this version

        (await SendAsync(EditOf(loaded.Id, loaded.Version, "B's edit"))).Succeeded.Should().BeTrue();
        var stale = await SendAsync(EditOf(loaded.Id, loaded.Version, "A's edit"));

        stale.Succeeded.Should().BeFalse();
        stale.ErrorMessage.Should().Be("This record was changed by someone else. Reload and try again.");
        ConcurrencyConflict.Is(stale).Should().BeTrue();
        (await FindAsync<PicklistSet>(loaded.Id))!.Text.Should().Be("B's edit");
    }

    // ---- 8 ------------------------------------------------------------------------------------

    [Test]
    public async Task T08_AnEditThatOmitsTheVersionFailsLoudly_AtValidationAndAtSave_AndMappingNeverWritesIt()
    {
        var saved = await CreateAsync();

        await FluentActions.Awaiting(() => SendAsync(EditOf(saved.Id, null, "no version")))
            .Should().ThrowAsync<ValidationException>().WithMessage("*versioned record*");

        // A handler that forgets ApplyLoadedVersion: inside a request, the save refuses.
        using (VersionChecks.EnterRequest())
        {
            await using var db = CreateDbContext();
            var row = await db.PicklistSets.FirstAsync(p => p.Id == saved.Id);
            row.Text = "forgot";
            await FluentActions.Awaiting(() => db.SaveChangesAsync(default))
                .Should().ThrowAsync<DataRuleViolationException>().WithMessage("*without the version the user loaded*");
        }

        // Mapping a command onto the entity never writes Version (its setter is private).
        var entity = new PicklistSet();
        new MapsterObjectMapper(MapsterConfiguration.Create()).Map(EditOf(saved.Id, 123456u, "mapped"), entity);
        entity.Version.Should().Be(0u);
        entity.Text.Should().Be("mapped");
    }

    // ---- 11 -----------------------------------------------------------------------------------

    [Test]
    public async Task T11_InsideAUnitOfWork_AConcurrencyConflictIsNotRetried()
    {
        var loaded = await CreateAsync();
        (await SendAsync(EditOf(loaded.Id, loaded.Version, "moved on"))).Succeeded.Should().BeTrue();

        using var scope = CreateScope();
        var attempts = 0;
        var act = async () => await TransactionKit.RunAsync(scope.ServiceProvider, new TxCommand(), async ct =>
        {
            attempts++;
            await using var db = await TransactionKit.ContextAsync(scope.ServiceProvider, ct);
            var row = await db.PicklistSets.FirstAsync(p => p.Id == loaded.Id, ct);
            row.Text = "stale in a transaction";
            db.ApplyLoadedVersion(row, loaded.Version);
            await db.SaveChangesAsync(ct);
            return Result<int>.Success(1);
        });

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        attempts.Should().Be(1, "a conflict is not transient; only 40001 and 40P01 retry (pass 58 T11 covers those)");
    }

    // ---- 34: B4 -------------------------------------------------------------------------------

    [Test]
    public async Task T34_B4_ConsecutiveSavesUseTheReturnedVersion_AndTheOriginalIsThenRefused()
    {
        var original = await CreateAsync();

        var first = await SendAsync(EditOf(original.Id, original.Version, "first"));
        first.Succeeded.Should().BeTrue(first.ErrorMessage);
        var second = await SendAsync(EditOf(original.Id, first.Data!.Version, "second"));
        second.Succeeded.Should().BeTrue(second.ErrorMessage);

        var third = await SendAsync(EditOf(original.Id, original.Version, "third"));
        ConcurrencyConflict.Is(third).Should().BeTrue("the form must replace the version it holds with the returned one");
    }

    [Test]
    public async Task T34_B4_InsideAUnitOfWork_TheReturnedVersionIsTheCommittedOne()
    {
        var original = await CreateAsync();
        using var scope = CreateScope();
        Saved<int>? saved = null;
        uint afterHandlerSave = 0;

        var result = await TransactionKit.RunAsync(scope.ServiceProvider, new TxCommand(), async ct =>
        {
            await using var db = await TransactionKit.ContextAsync(scope.ServiceProvider, ct);
            var row = await db.PicklistSets.FirstAsync(p => p.Id == original.Id, ct);
            db.ApplyLoadedVersion(row, original.Version);
            row.Text = "by the handler";
            // A posting handler updates the same row again, after the handler has returned.
            row.AddDomainEvent(new ScriptedPostingEvent("touch", async (sp, c) =>
            {
                await using var inner = await TransactionKit.ContextAsync(sp, c);
                var same = await inner.PicklistSets.FirstAsync(p => p.Id == original.Id, c);
                inner.UseCurrentVersion(same);
                same.Description = "by the posting handler";
                await inner.SaveChangesAsync(c);
            }));
            saved = db.SavedVersionOf(row, row.Id);
            await db.SaveChangesAsync(ct);
            afterHandlerSave = saved.Version;
            return Result<int>.Success(row.Id);
        });

        result.Succeeded.Should().BeTrue(result.ErrorMessage);
        var stored = (await FindAsync<PicklistSet>(original.Id))!;
        stored.Description.Should().Be("by the posting handler");
        saved!.Version.Should().Be(stored.Version, "the result was refreshed by the behaviour's later saves");
        saved.Version.Should().NotBe(afterHandlerSave, "a value copied at the handler's own save would have been stale");

        (await SendAsync(EditOf(original.Id, saved.Version, "next"))).Succeeded.Should().BeTrue();
    }

    // ---- 20: users ----------------------------------------------------------------------------

    [Test]
    public async Task T20_AUserIsNeverDeleted_AndTheAuditTrailKeepsItsReference()
    {
        var userId = await RunAsUserAsync("leaver", "Password123!", []);
        await CreateAsync();                                             // audited work by this user
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var audited = await db.AuditTrails.IgnoreQueryFilters().CountAsync(a => a.UserId == userId);
        audited.Should().BeGreaterThan(0);

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await userManager.FindByIdAsync(userId))!;
        await FluentActions.Awaiting(() => userManager.DeleteAsync(user))
            .Should().ThrowAsync<DataRuleViolationException>().WithMessage("*deactivated, never deleted*");

        await using (var connection = new NpgsqlConnection(Database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("DELETE FROM \"AspNetUsers\" WHERE \"Id\" = @id", connection);
            command.Parameters.AddWithValue("id", userId);
            (await FluentActions.Awaiting(() => command.ExecuteNonQueryAsync()).Should().ThrowAsync<PostgresException>())
                .Which.SqlState.Should().BeOneOf(PostgresErrorCodes.ForeignKeyViolation, PostgresErrorCodes.RestrictViolation);
        }

        await using var fresh = CreateDbContext();
        (await fresh.AuditTrails.IgnoreQueryFilters().CountAsync(a => a.UserId == userId)).Should().Be(audited);

        // The Users page has nothing to delete with: the permission and the access right are gone.
        typeof(Permissions.Users).GetField("Delete").Should().BeNull();
        typeof(UsersAccessRights).GetProperty("Delete").Should().BeNull();
    }

    // ---- 27: refresh --------------------------------------------------------------------------

    [Test]
    public async Task T27_RemovingAnAssignmentRefusesTheNextWriteAtOnce_AndTheNextLoadDropsItFromReads()
    {
        var branch = (await SendAsync(new AddEditBranchCommand { Code = "R" + Guid.NewGuid().ToString("N")[..6], Name = "Refresh" })).Data!.Id;
        var userId = await CreateLimitedUserAsync();

        (await SendAsync(new SetUserBranchesCommand { UserId = userId, BranchIds = [branch], DefaultBranchId = branch })).Succeeded.Should().BeTrue();
        using var scope = CreateScope();
        var loader = scope.ServiceProvider.GetRequiredService<IUserContextLoader>();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test"));
        (await loader.LoadAsync(principal))!.Branches!.ReadIds.Should().Contain(branch);

        (await SendAsync(new SetUserBranchesCommand { UserId = userId, BranchIds = [] })).Succeeded.Should().BeTrue();
        (await loader.LoadAsync(principal))!.Branches!.ReadIds.Should().NotContain(branch, "the command invalidated the cached context");

        // The write check reads the database itself, so the very next write is refused.
        var accessor = scope.ServiceProvider.GetRequiredService<IUserContextAccessor>();
        using (accessor.Push(new UserContext(userId, userId, Branches: new BranchAccess(false, [branch], [branch], false, branch))))
        {
            var behaviour = ActivatorUtilities.CreateInstance<BranchAccessBehaviour<BranchCmd, Result<int>>>(scope.ServiceProvider);
            await FluentActions.Awaiting(() => behaviour.Handle(new BranchCmd(branch), (_, _) => ValueTask.FromResult(Result<int>.Success(1)), default).AsTask())
                .Should().ThrowAsync<ForbiddenAccessException>();
        }

        // A role's ViewAll change reaches every member's next load.
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var role = new ApplicationRole("p59-viewall-" + Guid.NewGuid().ToString("N")[..6]);
        await roleManager.CreateAsync(role);
        await userManager.AddToRoleAsync((await userManager.FindByIdAsync(userId))!, role.Name!);
        loader.ClearUserContextCache(userId);
        (await loader.LoadAsync(principal))!.Branches!.ReadsAll.Should().BeFalse();
        await roleManager.AddClaimAsync(role, new Claim(ApplicationClaimTypes.Permission, Permissions.Branches.ViewAll));
        scope.ServiceProvider.GetRequiredService<IUserContextInvalidator>().InvalidateAll();
        (await loader.LoadAsync(principal))!.Branches!.ReadsAll.Should().BeTrue();

    }

    private static async Task<string> CreateLimitedUserAsync()
    {
        using var scope = CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var name = "limited-" + Guid.NewGuid().ToString("N")[..8];
        var user = new ApplicationUser { UserName = name, Email = name + "@example.com", IsActive = true };
        (await userManager.CreateAsync(user, "Password123!")).Succeeded.Should().BeTrue();
        return user.Id;
    }
}
