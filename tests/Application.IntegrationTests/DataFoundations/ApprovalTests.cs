using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Approvals;
using CleanArchitecture.Blazor.Application.Common.Branches;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.ExceptionHandlers;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Transactions;
using CleanArchitecture.Blazor.Application.Common.Models;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Application.Pipeline;

using CleanArchitecture.Blazor.Domain.Common.Entities;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Rules;
using CleanArchitecture.Blazor.Infrastructure.Services.Identity;
using FluentAssertions;
using Mediator;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using static CleanArchitecture.Blazor.Application.IntegrationTests.DataFoundations.ModelHarness;

namespace CleanArchitecture.Blazor.Application.IntegrationTests.DataFoundations;

/// <summary>The test application's approval permissions.</summary>
public static class TestApprovalPermissions
{
    [ApprovalPermission] public const string Approve = "Permissions.TestDocuments.Approve";

    [ApprovalPermission] public const string ApprovePlain = "Permissions.TestPlain.Approve";
}

/// <summary>Approves a branch-scoped test document.</summary>
[RequestAuthorize(Policy = TestApprovalPermissions.Approve)]
public sealed class ApproveTestDocumentCommand(Guid documentId, int branchId)
    : IRequest<Result<int>>, IApprovalCommand<TestApprovableDocument>, IIdempotentRequest, IBranchCommand
{
    public Guid DocumentId { get; } = documentId;
    public int BranchId { get; } = branchId;
    public Guid RequestId { get; init; } = Guid.NewGuid();
}

/// <summary>Amendment C1: approves a document with no branch scope, and is no branch command.</summary>
[RequestAuthorize(Policy = TestApprovalPermissions.ApprovePlain)]
public sealed class ApproveTestPlainCommand(Guid documentId) : IRequest<Result<int>>, IApprovalCommand<TestPlainApprovable>, IIdempotentRequest
{
    public Guid DocumentId { get; } = documentId;
    public Guid RequestId { get; init; } = Guid.NewGuid();
}

/// <summary>Row 13 / 35: a branch-scoped header approved by a command that is not a branch command.</summary>
[RequestAuthorize(Policy = TestApprovalPermissions.Approve)]
public sealed class ApproveWithoutBranchCommand(Guid documentId) : IRequest<Result<int>>, IApprovalCommand<TestApprovableDocument>, IIdempotentRequest
{
    public Guid DocumentId { get; } = documentId;
    public Guid RequestId { get; init; } = Guid.NewGuid();
}

/// <summary>Row 13: not idempotent.</summary>
[RequestAuthorize(Policy = TestApprovalPermissions.ApprovePlain)]
public sealed class ApproveNotIdempotentCommand(Guid documentId) : IRequest<Result<int>>, IApprovalCommand<TestPlainApprovable>
{
    public Guid DocumentId { get; } = documentId;
}

/// <summary>Row 13: authorized by an ordinary permission.</summary>
[RequestAuthorize(Policy = Permissions.PicklistSets.Edit)]
public sealed class ApproveByOrdinaryPermissionCommand(Guid documentId) : IRequest<Result<int>>, IApprovalCommand<TestPlainApprovable>, IIdempotentRequest
{
    public Guid DocumentId { get; } = documentId;
    public Guid RequestId { get; init; } = Guid.NewGuid();
}

/// <summary>Row 13: an edit carrying an approval permission.</summary>
[RequestAuthorize(Policy = TestApprovalPermissions.Approve)]
public sealed class EditDressedAsApprovalCommand : IRequest<Result<int>>;

/// <summary>
/// Pass 60 plan §5 rows 1 to 14, 34 and 35: the approval policy, through the real behaviours (authorization, branch,
/// transaction, approval) on the test model.
/// </summary>
[NonParallelizable]
public class ApprovalTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    [OneTimeSetUp]
    public async Task Ready() => await RootAsync();

    // ---- fixtures --------------------------------------------------------------------------------------

    private static string Tag() => "a" + Guid.NewGuid().ToString("N")[..10];

    /// <summary>A role holding <paramref name="permissions"/>, with a limit row per (documentType, branch, value).</summary>
    private static async Task<ApplicationRole> RoleAsync(string[] permissions, params (string Type, int? Branch, decimal Max)[] limits)
    {
        var services = await RootAsync();
        using var scope = services.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        var role = new ApplicationRole("p60-" + Tag());
        (await roleManager.CreateAsync(role)).Succeeded.Should().BeTrue();
        foreach (var permission in permissions)
            await roleManager.AddClaimAsync(role, new Claim(ApplicationClaimTypes.Permission, permission));

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        foreach (var (type, branch, max) in limits)
            db.ApprovalLimits.Add(new ApprovalLimit { RoleId = role.Id, DocumentType = type, BranchId = branch, MaxValue = max });
        await db.SaveChangesAsync();
        return role;
    }

    private static async Task<string> UserInAsync(int[] branches, params ApplicationRole[] roles)
    {
        var userId = await CreateUserAsync(branches);
        await AddToRolesAsync(userId, roles.Select(r => r.Name!).ToArray());
        return userId;
    }

    private static async Task AddToRolesAsync(string userId, params string[] roles)
    {
        var services = await RootAsync();
        using var scope = services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        foreach (var role in roles)
            if (!await roleManager.RoleExistsAsync(role)) await roleManager.CreateAsync(new ApplicationRole(role));
        var user = await userManager.FindByIdAsync(userId);
        (await userManager.AddToRolesAsync(user!, roles)).Succeeded.Should().BeTrue();
    }

    private static async Task<T> SeedAsync<T>(T entity, UserContext? user = null) where T : class
    {
        var services = await RootAsync();
        using var _ = Accessor.Push(user);
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        using (entity is TestApprovableDocument d ? BranchGrant.Begin(d.BranchId) : null)
        {
            db.Add(entity);
            await db.SaveChangesAsync();
        }
        return entity;
    }

    private static Task<TestApprovableDocument> DocumentAsync(int branch, decimal value, string preparedBy) =>
        SeedAsync(new TestApprovableDocument { BranchId = branch, ApprovalValue = value, PreparedById = preparedBy });

    private static async Task<T> ReadAsync<T>(Guid id) where T : class, IApprovable
    {
        using var _ = Accessor.Push(ModelHarness.System);
        var (scope, db) = await ContextAsync();
        using (scope)
        await using (db)
            return await db.Set<T>().AsNoTracking().SingleAsync(d => d.Id == id);
    }

    private static UserContext As(string userId, params int[] branches) => Reader(userId, Reads(branches));

    /// <summary>The handler a module would write: record the approver the policy checked.</summary>
    private static async Task<Result<int>> RecordApprovalAsync<THeader>(IServiceProvider services, Guid id, CancellationToken ct)
        where THeader : class, IApprovable
    {
        var context = services.GetRequiredService<IUnitOfWork>().Context;
        var header = await context.Set<THeader>().SingleAsync(h => h.Id == id, ct);
        var entry = context.Entry(header);
        entry.Property(nameof(IApprovable.ApprovedById)).CurrentValue = services.GetRequiredService<IUserContextAccessor>().Current!.UserId;
        entry.Property(nameof(IApprovable.ApprovedAt)).CurrentValue = DateTime.UtcNow;
        await context.SaveChangesAsync(ct);
        return Result<int>.Success(1);
    }

    /// <summary>Authorization → branch → transaction → approval → handler: the order the application composes.</summary>
    private static async Task<Result<int>> ApproveAsync(UserContext user, ApproveTestDocumentCommand command)
    {
        var services = await RootAsync();
        using var _ = Accessor.Push(user);
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var auth = ActivatorUtilities.CreateInstance<AuthorizationBehaviour<ApproveTestDocumentCommand, Result<int>>>(sp);
        var branch = ActivatorUtilities.CreateInstance<BranchAccessBehaviour<ApproveTestDocumentCommand, Result<int>>>(sp);
        var tx = ActivatorUtilities.CreateInstance<TransactionBehaviour<ApproveTestDocumentCommand, Result<int>>>(sp);
        var approval = ActivatorUtilities.CreateInstance<ApprovalBehaviour<ApproveTestDocumentCommand, Result<int>>>(sp);

        MessageHandlerDelegate<ApproveTestDocumentCommand, Result<int>> handler =
            (r, ct) => new ValueTask<Result<int>>(RecordApprovalAsync<TestApprovableDocument>(sp, r.DocumentId, ct));
        MessageHandlerDelegate<ApproveTestDocumentCommand, Result<int>> inner = (r, ct) => approval.Handle(r, handler, ct);
        MessageHandlerDelegate<ApproveTestDocumentCommand, Result<int>> transactional = (r, ct) => tx.Handle(r, inner, ct);
        MessageHandlerDelegate<ApproveTestDocumentCommand, Result<int>> branched = (r, ct) => branch.Handle(r, transactional, ct);
        return await auth.Handle(command, branched, CancellationToken.None).AsTask().WaitAsync(Guard);
    }

    private static async Task<Result<int>> ApprovePlainAsync(UserContext user, ApproveTestPlainCommand command)
    {
        var services = await RootAsync();
        using var _ = Accessor.Push(user);
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var auth = ActivatorUtilities.CreateInstance<AuthorizationBehaviour<ApproveTestPlainCommand, Result<int>>>(sp);
        var tx = ActivatorUtilities.CreateInstance<TransactionBehaviour<ApproveTestPlainCommand, Result<int>>>(sp);
        var approval = ActivatorUtilities.CreateInstance<ApprovalBehaviour<ApproveTestPlainCommand, Result<int>>>(sp);

        MessageHandlerDelegate<ApproveTestPlainCommand, Result<int>> handler =
            (r, ct) => new ValueTask<Result<int>>(RecordApprovalAsync<TestPlainApprovable>(sp, r.DocumentId, ct));
        MessageHandlerDelegate<ApproveTestPlainCommand, Result<int>> inner = (r, ct) => approval.Handle(r, handler, ct);
        MessageHandlerDelegate<ApproveTestPlainCommand, Result<int>> transactional = (r, ct) => tx.Handle(r, inner, ct);
        return await auth.Handle(command, transactional, CancellationToken.None).AsTask().WaitAsync(Guard);
    }

    /// <summary>A branch, a preparer, a Branch Manager (limit 1,000) and a Budget Holder (limit 10,000) in it.</summary>
    private sealed record Cast(int Branch, string Preparer, string Manager, string Holder, ApplicationRole ManagerRole, ApplicationRole HolderRole);

    private static async Task<Cast> CastAsync()
    {
        var branch = await CreateBranchAsync();
        var managerRole = await RoleAsync([TestApprovalPermissions.Approve], (TestApprovableDocument.ApprovalDocumentType, null, 1_000m));
        var holderRole = await RoleAsync([TestApprovalPermissions.Approve], (TestApprovableDocument.ApprovalDocumentType, null, 10_000m));
        return new Cast(branch, await CreateUserAsync([branch]), await UserInAsync([branch], managerRole),
            await UserInAsync([branch], holderRole), managerRole, holderRole);
    }

    // ---- 1 to 3 ----------------------------------------------------------------------------------------

    [Test]
    public async Task T01_APreparerCannotApproveTheirOwnDocument_NorItsCreator()
    {
        var cast = await CastAsync();
        var mine = await DocumentAsync(cast.Branch, 100m, preparedBy: cast.Manager);

        await FluentActions.Awaiting(() => ApproveAsync(As(cast.Manager, cast.Branch), new ApproveTestDocumentCommand(mine.Id, cast.Branch)))
            .Should().ThrowAsync<ForbiddenAccessException>().WithMessage("You prepared this test.document. Someone else must approve it.");
        (await ReadAsync<TestApprovableDocument>(mine.Id)).ApprovedById.Should().BeNull("nothing was saved");

        // The creator is barred too (decision 8), though someone else submitted it.
        TestApprovableDocument created;
        created = await SeedAsync(new TestApprovableDocument { BranchId = cast.Branch, ApprovalValue = 100m, PreparedById = cast.Preparer },
            As(cast.Holder, cast.Branch));
        (await ReadAsync<TestApprovableDocument>(created.Id)).CreatedById.Should().Be(cast.Holder);
        await FluentActions.Awaiting(() => ApproveAsync(As(cast.Holder, cast.Branch), new ApproveTestDocumentCommand(created.Id, cast.Branch)))
            .Should().ThrowAsync<ForbiddenAccessException>().WithMessage("You prepared this*");
    }

    [Test]
    public async Task T02_AnApproverWithinTheLimit_Succeeds()
    {
        var cast = await CastAsync();
        var document = await DocumentAsync(cast.Branch, 999.99m, cast.Preparer);
        var before = document.Version;

        var result = await ApproveAsync(As(cast.Manager, cast.Branch), new ApproveTestDocumentCommand(document.Id, cast.Branch));

        result.Succeeded.Should().BeTrue();
        var approved = await ReadAsync<TestApprovableDocument>(document.Id);
        approved.ApprovedById.Should().Be(cast.Manager);
        approved.ApprovedAt.Should().NotBeNull();
        approved.Version.Should().NotBe(before);
    }

    [Test]
    public async Task T03_AboveTheLimitIsRefused_AndAHigherRoleApproves()
    {
        var cast = await CastAsync();
        var document = await DocumentAsync(cast.Branch, 1_500m, cast.Preparer);

        await FluentActions.Awaiting(() => ApproveAsync(As(cast.Manager, cast.Branch), new ApproveTestDocumentCommand(document.Id, cast.Branch)))
            .Should().ThrowAsync<ForbiddenAccessException>()
            .WithMessage("This test.document is 1,500.00. Your approval limit is 1,000.00, so it needs an approver whose limit covers it.");

        (await ApproveAsync(As(cast.Holder, cast.Branch), new ApproveTestDocumentCommand(document.Id, cast.Branch))).Succeeded.Should().BeTrue();
        (await ReadAsync<TestApprovableDocument>(document.Id)).ApprovedById.Should().Be(cast.Holder);
    }

    // ---- 4 to 7 ----------------------------------------------------------------------------------------

    [Test]
    public async Task T04_TheAdministratorIsRefused_EvenHoldingAnApprovingRole_AndSoIsTheSystem()
    {
        var cast = await CastAsync();
        var administrator = await UserInAsync([cast.Branch], cast.HolderRole); // seeded directly, past the UI refusal
        await AddToRolesAsync(administrator, Roles.Admin);
        var document = await DocumentAsync(cast.Branch, 10m, cast.Preparer);

        await FluentActions.Awaiting(() => ApproveAsync(As(administrator, cast.Branch), new ApproveTestDocumentCommand(document.Id, cast.Branch)))
            .Should().ThrowAsync<ForbiddenAccessException>().WithMessage("The administrator account cannot approve documents.");

        // The system context: decided by the policy itself.
        var services = await RootAsync();
        using var _ = Accessor.Push(ModelHarness.System);
        using var scope = services.CreateScope();
        await using var session = await scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>().BeginAsync(CancellationToken.None);
        var decision = await scope.ServiceProvider.GetRequiredService<IApprovalPolicy>()
            .DecideAsync(document, TestApprovableDocument.ApprovalDocumentType, cast.Holder, cast.Branch, CancellationToken.None);
        decision.Refusal.Should().Be(ApprovalRefusal.System);
        decision.Message.Should().Be("Background work cannot approve documents.");
    }

    [Test]
    public async Task T05_ARoleWithoutALimitHasNoAuthority_AndABranchRowOverridesTheGeneralRow()
    {
        var cast = await CastAsync();
        var noLimitRole = await RoleAsync([TestApprovalPermissions.Approve]);
        var unlimited = await UserInAsync([cast.Branch], noLimitRole);
        var document = await DocumentAsync(cast.Branch, 10m, cast.Preparer);

        await FluentActions.Awaiting(() => ApproveAsync(As(unlimited, cast.Branch), new ApproveTestDocumentCommand(document.Id, cast.Branch)))
            .Should().ThrowAsync<ForbiddenAccessException>().WithMessage("You have no approval limit for test.document.");

        // A role: 100 in general, 5,000 in branch B only.
        var other = await CreateBranchAsync();
        var role = await RoleAsync([TestApprovalPermissions.Approve],
            (TestApprovableDocument.ApprovalDocumentType, null, 100m), (TestApprovableDocument.ApprovalDocumentType, other, 5_000m));
        var approver = await UserInAsync([cast.Branch, other], role);

        (await ApproveAsync(As(approver, cast.Branch, other),
            new ApproveTestDocumentCommand((await DocumentAsync(other, 4_000m, cast.Preparer)).Id, other))).Succeeded.Should().BeTrue();
        await FluentActions.Awaiting(() => ApproveAsync(As(approver, cast.Branch, other),
                new ApproveTestDocumentCommand(document.Id, cast.Branch) ))
            .Should().NotThrowAsync("10 is within the general row of 100");
        var big = await DocumentAsync(cast.Branch, 4_000m, cast.Preparer);
        await FluentActions.Awaiting(() => ApproveAsync(As(approver, cast.Branch, other), new ApproveTestDocumentCommand(big.Id, cast.Branch)))
            .Should().ThrowAsync<ForbiddenAccessException>().WithMessage("*limit is 100.00*", "the branch row applies in its branch only");
    }

    [Test]
    public async Task T06_LoweringALimit_OrLeavingTheRole_RefusesTheNextApprovalAtOnce()
    {
        var cast = await CastAsync();
        (await ApproveAsync(As(cast.Manager, cast.Branch),
            new ApproveTestDocumentCommand((await DocumentAsync(cast.Branch, 500m, cast.Preparer)).Id, cast.Branch))).Succeeded.Should().BeTrue();

        var services = await RootAsync();
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var limit = await db.ApprovalLimits.SingleAsync(l => l.RoleId == cast.ManagerRole.Id);
            limit.MaxValue = 100m;
            await db.SaveChangesAsync();
        }

        var second = await DocumentAsync(cast.Branch, 500m, cast.Preparer);
        await FluentActions.Awaiting(() => ApproveAsync(As(cast.Manager, cast.Branch), new ApproveTestDocumentCommand(second.Id, cast.Branch)))
            .Should().ThrowAsync<ForbiddenAccessException>().WithMessage("*limit is 100.00*");

        using (var scope = services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var holder = await userManager.FindByIdAsync(cast.Holder);
            await userManager.RemoveFromRoleAsync(holder!, cast.HolderRole.Name!);
        }
        var third = await DocumentAsync(cast.Branch, 50m, cast.Preparer);
        await FluentActions.Awaiting(() => ApproveAsync(As(cast.Holder, cast.Branch), new ApproveTestDocumentCommand(third.Id, cast.Branch)))
            .Should().ThrowAsync<ForbiddenAccessException>("without the role the user has neither the permission nor a limit");
    }

    [Test]
    public async Task T07_ADocumentOfAnotherBranch_IsRefused_AndAnUnassignedBranchIsRefusedFirst()
    {
        var cast = await CastAsync();
        var elsewhere = await CreateBranchAsync();
        var document = await DocumentAsync(elsewhere, 10m, cast.Preparer);

        // An approver who can read both branches names the wrong one: refused by the policy (rule 5).
        var both = await UserInAsync([cast.Branch, elsewhere], cast.HolderRole);
        await FluentActions.Awaiting(() => ApproveAsync(As(both, cast.Branch, elsewhere), new ApproveTestDocumentCommand(document.Id, cast.Branch)))
            .Should().ThrowAsync<ForbiddenAccessException>().WithMessage("This test.document belongs to branch *, not *.");

        // (P60) An approver who cannot read the document's branch does not find it: the header is loaded through the
        // branch filter, so a document outside the read set is not revealed, even by a refusal.
        await FluentActions.Awaiting(() => ApproveAsync(As(cast.Manager, cast.Branch), new ApproveTestDocumentCommand(document.Id, cast.Branch)))
            .Should().ThrowAsync<NotFoundException>();


        await FluentActions.Awaiting(() => ApproveAsync(As(cast.Manager, cast.Branch), new ApproveTestDocumentCommand(document.Id, elsewhere)))
            .Should().ThrowAsync<ForbiddenAccessException>().WithMessage("You are not assigned to branch *.");
    }

    // ---- 8 to 10 ---------------------------------------------------------------------------------------

    [Test]
    public async Task T08_ApprovingOutsideAnApprovalCommand_FailsAtSave_AsDoesClearingAnApproval()
    {
        var cast = await CastAsync();
        var document = await DocumentAsync(cast.Branch, 10m, cast.Preparer);

        using (Accessor.Push(As(cast.Holder, cast.Branch)))
        using (BranchGrant.Begin(cast.Branch))
        {
            var (scope, db) = await ContextAsync();
            using (scope)
            await using (db)
            {
                var loaded = await db.Set<TestApprovableDocument>().SingleAsync(d => d.Id == document.Id);
                loaded.ApprovedById = cast.Holder;
                loaded.ApprovedAt = DateTime.UtcNow;
                await FluentActions.Awaiting(() => db.SaveChangesAsync()).Should().ThrowAsync<DataRuleViolationException>()
                    .WithMessage("*was approved outside an approval command. Implement IApprovalCommand<TestApprovableDocument>*");
            }
        }

        (await ApproveAsync(As(cast.Holder, cast.Branch), new ApproveTestDocumentCommand(document.Id, cast.Branch))).Succeeded.Should().BeTrue();
        using (Accessor.Push(As(cast.Holder, cast.Branch)))
        using (BranchGrant.Begin(cast.Branch))
        {
            var (scope, db) = await ContextAsync();
            using (scope)
            await using (db)
            {
                var loaded = await db.Set<TestApprovableDocument>().SingleAsync(d => d.Id == document.Id);
                loaded.ApprovedById = null;
                loaded.ApprovedAt = null;
                await FluentActions.Awaiting(() => db.SaveChangesAsync()).Should().ThrowAsync<DataRuleViolationException>()
                    .WithMessage("*outside an approval command*");
            }
        }
    }

    [Test]
    public async Task T09_TwoApproversAtOnce_OneApproves_TheOtherIsToldItIsApproved()
    {
        var cast = await CastAsync();
        var second = await UserInAsync([cast.Branch], cast.HolderRole);
        var document = await DocumentAsync(cast.Branch, 10m, cast.Preparer);

        var a = Task.Run(() => ApproveAsync(As(cast.Holder, cast.Branch), new ApproveTestDocumentCommand(document.Id, cast.Branch)));
        var b = Task.Run(() => ApproveAsync(As(second, cast.Branch), new ApproveTestDocumentCommand(document.Id, cast.Branch)));
        var outcomes = await Task.WhenAll(Outcome(a), Outcome(b));

        outcomes.Count(o => o == "ok").Should().Be(1);
        outcomes.Should().ContainSingle(o => o == "This test.document is already approved.");

        static async Task<string> Outcome(Task<Result<int>> attempt)
        {
            try { return (await attempt).Succeeded ? "ok" : "failed"; }
            catch (ForbiddenAccessException e) { return e.Message; }
        }
    }

    [Test]
    public async Task T10_ARepeatedRequestIdApprovesOnce_AndARefusedAttemptRecordsNothing()
    {
        var cast = await CastAsync();
        var document = await DocumentAsync(cast.Branch, 10m, cast.Preparer);
        var command = new ApproveTestDocumentCommand(document.Id, cast.Branch);

        (await ApproveAsync(As(cast.Holder, cast.Branch), command)).Succeeded.Should().BeTrue();
        (await ApproveAsync(As(cast.Holder, cast.Branch), command)).Succeeded.Should().BeTrue(
            "the recorded response is replayed; the policy would otherwise say 'already approved'");

        // Refused, then the limit is raised, and the SAME RequestId succeeds: nothing was recorded for the refusal.
        var big = await DocumentAsync(cast.Branch, 5_000m, cast.Preparer);
        var retried = new ApproveTestDocumentCommand(big.Id, cast.Branch);
        await FluentActions.Awaiting(() => ApproveAsync(As(cast.Manager, cast.Branch), retried)).Should().ThrowAsync<ForbiddenAccessException>();
        var services = await RootAsync();
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.ApprovalLimits.SingleAsync(l => l.RoleId == cast.ManagerRole.Id)).MaxValue = 10_000m;
            await db.SaveChangesAsync();
        }
        (await ApproveAsync(As(cast.Manager, cast.Branch), retried)).Succeeded.Should().BeTrue();
    }

    // ---- 11 to 14, 35 ----------------------------------------------------------------------------------

    [Test]
    public async Task T11_TheAdministratorCannotBeGivenApprovalRights()
    {
        var services = await RootAsync();
        var cast = await CastAsync();
        var administrator = await CreateUserAsync([]);
        await AddToRolesAsync(administrator, Roles.Admin);

        // An actor who may grant the permission (they hold it), granting it to the administrator.
        var actor = await UserInAsync([], cast.HolderRole);
        using var _ = Accessor.Push(Reader(actor, Reads()));
        using var scope = services.CreateScope();
        var assignments = scope.ServiceProvider.GetRequiredService<PermissionAssignmentService>();

        var grant = new PermissionModel
        {
            UserId = administrator, ClaimType = ApplicationClaimTypes.Permission, ClaimValue = TestApprovalPermissions.Approve, Assigned = true
        };
        await FluentActions.Awaiting(() => assignments.AssignUserAsync(grant)).Should().ThrowAsync<ForbiddenAccessException>()
            .WithMessage("The administrator cannot be given approval rights.");

        // The administrator role itself: every permission on it is protected.
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        var adminRole = await roleManager.FindByNameAsync(Roles.Admin);
        await FluentActions.Awaiting(() => assignments.AssignRoleAsync(new PermissionModel
            {
                RoleId = adminRole!.Id, ClaimType = ApplicationClaimTypes.Permission, ClaimValue = TestApprovalPermissions.Approve, Assigned = true
            }))
            .Should().ThrowAsync<ForbiddenAccessException>().WithMessage("*protected*");

        // Role membership: Admin plus an approving role is refused, either way round.
        var protection = scope.ServiceProvider.GetRequiredService<AdministratorProtectionService>();
        await FluentActions.Awaiting(() => protection.EnsureApprovalSeparationAsync(null, [Roles.Admin, cast.HolderRole.Name!]))
            .Should().ThrowAsync<ForbiddenAccessException>().WithMessage("*approves documents*cannot hold both*");
        await FluentActions.Awaiting(() => protection.EnsureApprovalSeparationAsync(null, [cast.HolderRole.Name!]))
            .Should().NotThrowAsync();
    }

    [Test]
    public void T12_ARegistryGrantingAnApprovalPermission_FailsStartup()
    {
        FluentActions.Invoking(() => AdministratorPermissionRegistry.ValidateApprovals(
                [TestApprovalPermissions.Approve], [TestApprovalPermissions.Approve], []))
            .Should().Throw<InvalidOperationException>().WithMessage($"*in Granted: {TestApprovalPermissions.Approve}*");
        FluentActions.Invoking(() => AdministratorPermissionRegistry.ValidateApprovals([TestApprovalPermissions.Approve], [], []))
            .Should().Throw<InvalidOperationException>().WithMessage("*must be in Excluded*");
        FluentActions.Invoking(() => AdministratorPermissionRegistry.ValidateApprovals(
                [TestApprovalPermissions.Approve], [], [TestApprovalPermissions.Approve]))
            .Should().NotThrow();
        FluentActions.Invoking(AdministratorPermissionRegistry.AssertNoDivergence).Should().NotThrow("the template's own lists");
    }

    [Test]
    public void T13_T35_TheApprovalRequestGuard()
    {
        var problems = RequestContractRegistry.FindProblems(
        [
            typeof(ApproveTestDocumentCommand), typeof(ApproveTestPlainCommand), typeof(ApproveWithoutBranchCommand),
            typeof(ApproveNotIdempotentCommand), typeof(ApproveByOrdinaryPermissionCommand), typeof(EditDressedAsApprovalCommand)
        ]);

        problems.Should().HaveCount(4);
        problems.Should().Contain(p => p.Contains(nameof(ApproveWithoutBranchCommand)) && p.Contains("branch-scoped TestApprovableDocument and must implement IBranchCommand"));
        problems.Should().Contain(p => p.Contains(nameof(ApproveNotIdempotentCommand)) && p.Contains("not IIdempotentRequest"));
        problems.Should().Contain(p => p.Contains(nameof(ApproveByOrdinaryPermissionCommand)) && p.Contains("not marked [ApprovalPermission]"));
        problems.Should().Contain(p => p.Contains(nameof(EditDressedAsApprovalCommand)) && p.Contains("is not an IApprovalCommand"));
        problems.Should().NotContain(p => p.Contains(nameof(ApproveTestPlainCommand) + ":") || p.Contains(nameof(ApproveTestPlainCommand) + " "),
            "C1: a header with no branch scope may be approved by a command that is no branch command");
    }

    [Test]
    public async Task T14_TheModelAcceptsAnApprovableWithNoBranchScope()
    {
        var (scope, db) = await ContextAsync();
        using (scope)
        await using (db)
        {
            DataModelGuard.FindProblems(db.Model).Should().BeEmpty();
            scope.ServiceProvider.GetRequiredService<IApprovalDocumentTypes>().All.Should().Equal("test.document", "test.plain");
        }
    }

    // ---- 34: C1 ----------------------------------------------------------------------------------------

    [Test]
    public async Task T34_C1_ADocumentWithNoBranch_IsApprovedWithinTheLimit_AndRefusedAboveIt()
    {
        var branch = await CreateBranchAsync();
        var role = await RoleAsync([TestApprovalPermissions.ApprovePlain],
            (TestPlainApprovable.ApprovalDocumentType, null, 1_000m),
            (TestPlainApprovable.ApprovalDocumentType, branch, 50_000m)); // ignored: the document has no branch
        var approver = await UserInAsync([branch], role);
        var preparer = await CreateUserAsync([]);

        var within = await SeedAsync(new TestPlainApprovable { ApprovalValue = 900m, PreparedById = preparer });
        (await ApprovePlainAsync(As(approver, branch), new ApproveTestPlainCommand(within.Id))).Succeeded.Should().BeTrue();
        (await ReadAsync<TestPlainApprovable>(within.Id)).ApprovedById.Should().Be(approver);

        var above = await SeedAsync(new TestPlainApprovable { ApprovalValue = 2_000m, PreparedById = preparer });
        await FluentActions.Awaiting(() => ApprovePlainAsync(As(approver, branch), new ApproveTestPlainCommand(above.Id)))
            .Should().ThrowAsync<ForbiddenAccessException>().WithMessage("*limit is 1,000.00*");
    }
}
