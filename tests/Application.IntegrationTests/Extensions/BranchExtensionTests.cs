using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Branches;
using CleanArchitecture.Blazor.Application.Common.Concurrency;
using CleanArchitecture.Blazor.Application.Common.ExceptionHandlers;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Transactions;
using CleanArchitecture.Blazor.Application.Common.Models;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Application.Features.Branches;
using CleanArchitecture.Blazor.Application.IntegrationTests.DataFoundations;
using CleanArchitecture.Blazor.Application.Pipeline;
using CleanArchitecture.Blazor.Domain.Common;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Domain.Events;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Rules;
using CleanArchitecture.Blazor.TestSupport;
using FluentAssertions;
using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NUnit.Framework;
using static CleanArchitecture.Blazor.Application.IntegrationTests.DataFoundations.ModelHarness;

namespace CleanArchitecture.Blazor.Application.IntegrationTests.Extensions;

/// <summary>
/// Records the branch events and runs a test's script for them. Registered in every harness by the test assembly's
/// posting-handler scan, so it does nothing but record unless a test arms <see cref="Script"/>.
/// </summary>
public static class BranchEventProbe
{
    public static readonly List<PostingEvent> Seen = new();
    public static Func<IServiceProvider, PostingEvent, CancellationToken, Task>? Script;

    public static void Reset()
    {
        Seen.Clear();
        Script = null;
    }
}

public sealed class BranchEventProbeHandler(IServiceProvider services) :
    IPostingEventHandler<BranchCreatedEvent>, IPostingEventHandler<BranchChangedEvent>, IPostingEventHandler<BranchDeactivatingEvent>
{
    public ValueTask Handle(BranchCreatedEvent postingEvent, CancellationToken cancellationToken) => Run(postingEvent, cancellationToken);
    public ValueTask Handle(BranchChangedEvent postingEvent, CancellationToken cancellationToken) => Run(postingEvent, cancellationToken);
    public ValueTask Handle(BranchDeactivatingEvent postingEvent, CancellationToken cancellationToken) => Run(postingEvent, cancellationToken);

    private async ValueTask Run(PostingEvent postingEvent, CancellationToken cancellationToken)
    {
        BranchEventProbe.Seen.Add(postingEvent);
        if (BranchEventProbe.Script is { } script) await script(services, postingEvent, cancellationToken);
    }
}

/// <summary>A module command that defines a branch by nesting AddEditBranchCommand, then writes the new branch's row (row 12).</summary>
public sealed class DefineBranchWithRowCommand : IRequest<Result<int>>, ITransactionalRequest, IBranchDefiningCommand
{
    public string Code { get; init; } = string.Empty;
}

/// <summary>Pass 60b plan §5 rows 9 to 12: branch events, veto and the branch-defining grant (item 4, decision 9).</summary>
[NonParallelizable]
public class BranchExtensionTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    [OneTimeSetUp]
    public async Task Ready() => await RootAsync();

    [SetUp]
    public void Arm() { BranchEventProbe.Reset(); _tag = TestBranches.NewTag(); }

    [TearDown]
    public void Disarm() => BranchEventProbe.Reset();

    // Pass 60c (T9): codes and names any application's tightened branch rule accepts (TestBranches.CodeFloor), and
    // names unique in the model database, which is never reset between tests. One tag per test keeps a renamed branch's
    // name stable within the test.
    private static string Code() => TestBranches.NewCode();

    private static string _tag = TestBranches.NewTag();

    private static string N(string label) => label + " " + _tag;

    /// <summary>A branch maintainer with no branch assignment, as the administrator is.</summary>
    private static async Task<UserContext> MaintainerAsync()
    {
        var userId = await CreateUserAsync([], Permissions.Branches.View, Permissions.Branches.Create, Permissions.Branches.Edit);
        return Reader(userId, Reads());
    }

    private static async Task<T> SendAsync<T>(UserContext user, IRequest<T> request)
    {
        var services = await RootAsync();
        using var _ = Accessor.Push(user);
        using var scope = services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request).AsTask().WaitAsync(Guard);
    }

    private static async Task<Branch> BranchAsync(int id)
    {
        var (scope, db) = await ContextAsync();
        using (scope)
        await using (db)
            return await db.Branches.AsNoTracking().SingleAsync(b => b.Id == id);
    }

    private static async Task<int> RowsInAsync(int branchId)
    {
        var (scope, db) = await ContextAsync();
        using (scope)
        await using (db)
        using (Accessor.Push(ModelHarness.System))
            return await db.TestBranchRows.CountAsync(r => r.BranchId == branchId);
    }

    private static async Task WriteRowAsync(IServiceProvider services, int branchId, CancellationToken ct)
    {
        var db = services.GetRequiredService<IUnitOfWork>().Context;
        db.Set<TestBranchRow>().Add(new TestBranchRow { BranchId = branchId, Name = "default location" });
        await db.SaveChangesAsync(ct);
    }

    // ---- 9 ---------------------------------------------------------------------------------------------

    [Test]
    public async Task T09_EachChange_RaisesItsEvent_InTheTransaction_AndNoChangeRaisesNothing()
    {
        var user = await MaintainerAsync();
        var code = Code();

        var created = await SendAsync(user, new AddEditBranchCommand { Code = code, Name = N("North") });
        created.Succeeded.Should().BeTrue(created.ErrorMessage);
        var id = created.Data!.Id;
        BranchEventProbe.Seen.Should().ContainSingle().Which.Should().BeOfType<BranchCreatedEvent>()
            .Which.BranchId.Should().Be(id, "the id is read after the save");

        BranchEventProbe.Seen.Clear();
        var renamed = await SendAsync(user, new AddEditBranchCommand { Id = id, Code = code, Name = N("North Yard"), Version = created.Data.Version });
        renamed.Succeeded.Should().BeTrue(renamed.ErrorMessage);
        var changed = BranchEventProbe.Seen.Should().ContainSingle().Which.Should().BeOfType<BranchChangedEvent>().Subject;
        (changed.OldName, changed.Name, changed.Reactivated).Should().Be((N("North"), N("North Yard"), false));

        BranchEventProbe.Seen.Clear();
        var deactivated = await SendAsync(user, new AddEditBranchCommand
            { Id = id, Code = code, Name = N("North Yard"), IsActive = false, Version = renamed.Data!.Version });
        deactivated.Succeeded.Should().BeTrue(deactivated.ErrorMessage);
        BranchEventProbe.Seen.Should().ContainSingle().Which.Should().BeOfType<BranchDeactivatingEvent>().Which.BranchId.Should().Be(id);

        BranchEventProbe.Seen.Clear();
        var reactivated = await SendAsync(user, new AddEditBranchCommand
            { Id = id, Code = code, Name = N("North Yard"), IsActive = true, Version = deactivated.Data!.Version });
        reactivated.Succeeded.Should().BeTrue(reactivated.ErrorMessage);
        BranchEventProbe.Seen.Should().ContainSingle().Which.Should().BeOfType<BranchChangedEvent>().Which.Reactivated.Should().BeTrue();

        BranchEventProbe.Seen.Clear();
        (await SendAsync(user, new AddEditBranchCommand
            { Id = id, Code = code, Name = N("North Yard"), IsActive = true, Version = reactivated.Data!.Version })).Succeeded.Should().BeTrue();
        BranchEventProbe.Seen.Should().BeEmpty("an edit that changes nothing raises nothing");
    }

    // ---- 10 --------------------------------------------------------------------------------------------

    [Test]
    public async Task T10_AHandlerVetoesADeactivation_AndNothingIsSaved()
    {
        var user = await MaintainerAsync();
        var code = Code();
        var created = (await SendAsync(user, new AddEditBranchCommand { Code = code, Name = N("South") })).Data!;

        BranchEventProbe.Script = (_, e, _) => e is BranchDeactivatingEvent
            ? throw new PostingRejectedException($"Branch {code} still has open stock counts.")
            : Task.CompletedTask;

        var result = await SendAsync(user, new AddEditBranchCommand
            { Id = created.Id, Code = code, Name = N("South renamed"), IsActive = false, Version = created.Version });

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Be($"Branch {code} still has open stock counts.");
        var stored = await BranchAsync(created.Id);
        (stored.IsActive, stored.Name, stored.Version).Should().Be((true, N("South"), created.Version), "everything rolled back");
    }

    // ---- 11 --------------------------------------------------------------------------------------------

    [Test]
    public async Task T11_ACreatedHandler_WritesTheNewBranchsRow_ForAMaintainerWithNoAssignment()
    {
        var user = await MaintainerAsync();
        BranchEventProbe.Script = (sp, e, ct) => e is BranchCreatedEvent c ? WriteRowAsync(sp, c.BranchId, ct) : Task.CompletedTask;

        var created = await SendAsync(user, new AddEditBranchCommand { Code = Code(), Name = N("East") });

        created.Succeeded.Should().BeTrue(created.ErrorMessage);
        (await RowsInAsync(created.Data!.Id)).Should().Be(1);
    }

    [Test]
    public async Task T11_AHandlerWritingAnotherBranchsRow_FailsTheCommand_AndNothingIsSaved()
    {
        var user = await MaintainerAsync();
        var other = await CreateBranchAsync();
        var code = Code();
        BranchEventProbe.Script = (sp, e, ct) => e is BranchCreatedEvent ? WriteRowAsync(sp, other, ct) : Task.CompletedTask;

        var created = await SendAsync(user, new AddEditBranchCommand { Code = code, Name = N("West") });

        created.Succeeded.Should().BeFalse();
        (await RowsInAsync(other)).Should().Be(0);
        var (scope, db) = await ContextAsync();
        using (scope)
        await using (db)
            (await db.Branches.AnyAsync(b => b.Code == code)).Should().BeFalse("the branch rolled back with the row");
    }

    [Test]
    public async Task T11_TheSaveRule_NamesTheGrant_BeforeAndAfterTheBranchIsSaved()
    {
        var (first, second) = (await CreateBranchAsync(), await CreateBranchAsync());
        var (scope, db) = await ContextAsync();
        using (scope)
        await using (db)
        using (BranchGrant.BeginDefining())
        {
            db.TestBranchRows.Add(new TestBranchRow { BranchId = first, Name = "too early" });
            await FluentActions.Awaiting(() => db.SaveChangesAsync()).Should().ThrowAsync<DataRuleViolationException>()
                .WithMessage("*defines a branch and has not saved it yet, so no branch is granted yet*");
            db.ChangeTracker.Clear();

            BranchGrant.ExtendDefining(first);
            db.TestBranchRows.Add(new TestBranchRow { BranchId = second, Name = "wrong branch" });
            await FluentActions.Awaiting(() => db.SaveChangesAsync()).Should().ThrowAsync<DataRuleViolationException>()
                .WithMessage($"*belongs to branch {second}, but this command was granted branch(es) {first}*");
        }

        FluentActions.Invoking(() => BranchGrant.ExtendDefining(first)).Should().Throw<InvalidOperationException>()
            .WithMessage("*outside a branch-defining command*IBranchDefiningCommand*");
    }

    [Test]
    public async Task T11_ARetriedAttempt_WritesTheRowOnce()
    {
        var user = await MaintainerAsync();
        var attempts = 0;
        BranchEventProbe.Script = async (sp, e, ct) =>
        {
            if (e is not BranchCreatedEvent c) return;
            if (Interlocked.Increment(ref attempts) == 1)
                throw new PostgresException("deadlock detected (injected)", "ERROR", "ERROR", "40P01");
            await WriteRowAsync(sp, c.BranchId, ct);
        };

        var created = await SendAsync(user, new AddEditBranchCommand { Code = Code(), Name = N("Retry") });

        created.Succeeded.Should().BeTrue(created.ErrorMessage);
        attempts.Should().Be(2);
        (await RowsInAsync(created.Data!.Id)).Should().Be(1);
    }

    // ---- 12 --------------------------------------------------------------------------------------------

    private static async Task<Result<int>> DefineAsync(UserContext user, string code, bool marked)
    {
        var services = await RootAsync();
        using var _ = Accessor.Push(user);
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var tx = ActivatorUtilities.CreateInstance<TransactionBehaviour<DefineBranchWithRowCommand, Result<int>>>(sp);
        var grant = new BranchDefinitionGrantBehaviour<DefineBranchWithRowCommand, Result<int>>();

        MessageHandlerDelegate<DefineBranchWithRowCommand, Result<int>> handler = async (r, ct) =>
        {
            var saved = await sp.GetRequiredService<IMediator>().Send(new AddEditBranchCommand { Code = r.Code, Name = N("Defined") }, ct);
            if (!saved.Succeeded) return Result<int>.Failure(saved.Errors);
            await WriteRowAsync(sp, saved.Data!.Id, ct); // after the nested send has returned
            return Result<int>.Success(saved.Data.Id);
        };
        MessageHandlerDelegate<DefineBranchWithRowCommand, Result<int>> transactional = (r, ct) => tx.Handle(r, handler, ct);
        var command = new DefineBranchWithRowCommand { Code = code };
        return marked
            ? await grant.Handle(command, transactional, CancellationToken.None).AsTask().WaitAsync(Guard)
            : await transactional(command, CancellationToken.None).AsTask().WaitAsync(Guard);
    }

    [Test]
    public async Task T12_AWrappingBranchDefiningCommand_KeepsTheGrant_AfterTheNestedSendReturns()
    {
        var user = await MaintainerAsync();
        var result = await DefineAsync(user, Code(), marked: true);

        result.Succeeded.Should().BeTrue(result.ErrorMessage);
        (await RowsInAsync(result.Data)).Should().Be(1);
    }

    [Test]
    public async Task T12_WithoutTheMarker_TheWrappersWriteFails_AndNothingIsSaved()
    {
        var user = await MaintainerAsync();
        var code = Code();

        await FluentActions.Awaiting(() => DefineAsync(user, code, marked: false))
            .Should().ThrowAsync<DataRuleViolationException>().WithMessage("*saved outside a branch command*");
        var (scope, db) = await ContextAsync();
        using (scope)
        await using (db)
            (await db.Branches.AnyAsync(b => b.Code == code)).Should().BeFalse();
    }
}

/// <summary>
/// A module's tightening of the branch rules (row 13): codes are exactly three capital letters, and names are unique.
/// Registered in the model harness beside the template's validator; inert unless <see cref="Enabled"/>.
/// </summary>
public sealed class TestModuleBranchValidator : FluentValidation.AbstractValidator<AddEditBranchCommand>
{
    public static bool Enabled;

    public const string CodeMessage = "A branch code is exactly three capital letters.";
    public const string NameMessage = "Another branch already has this name.";

    public TestModuleBranchValidator(CleanArchitecture.Blazor.Application.Common.Interfaces.IApplicationDbContextFactory dbContextFactory)
    {
        When(_ => Enabled, () =>
        {
            RuleFor(c => c.Code).Matches("^[A-Z]{3}$").WithMessage(CodeMessage);
            RuleFor(c => c.Name).MustAsync(async (command, name, ct) =>
            {
                await using var db = await dbContextFactory.CreateAsync(ct);
                return !await db.Branches.AnyAsync(b => b.Name == name.Trim() && b.Id != command.Id, ct);
            }).WithMessage(NameMessage);
        });
    }
}

/// <summary>Pass 60b plan §5 row 13: a module tightens the branch rules without editing the template's validator.</summary>
[NonParallelizable]
public class BranchRuleTighteningTests
{
    [OneTimeSetUp]
    public async Task Ready() => await RootAsync();

    [TearDown]
    public void Off() => TestModuleBranchValidator.Enabled = false;

    private static async Task<Result<Saved<int>>> SendAsync(AddEditBranchCommand command)
    {
        var userId = await CreateUserAsync([], Permissions.Branches.Create, Permissions.Branches.Edit);
        var services = await RootAsync();
        using var _ = Accessor.Push(Reader(userId, Reads()));
        using var scope = services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(command);
    }

    [Test]
    public async Task T13_AModulesValidator_RefusesThroughSend_AndTheTemplatesStillApplies()
    {
        TestModuleBranchValidator.Enabled = true;

        await FluentActions.Awaiting(() => SendAsync(new AddEditBranchCommand { Code = "AB", Name = "Tight " + Guid.NewGuid().ToString("N")[..6] }))
            .Should().ThrowAsync<FluentValidation.ValidationException>().WithMessage("*" + TestModuleBranchValidator.CodeMessage + "*");

        // The template's own rule, alongside.
        await FluentActions.Awaiting(() => SendAsync(new AddEditBranchCommand { Code = "ABC", Name = "" }))
            .Should().ThrowAsync<FluentValidation.ValidationException>().WithMessage("*Name*empty*");

        // A unique name, read from the database.
        var name = "Unique " + Guid.NewGuid().ToString("N")[..6];
        var code = new string(Guid.NewGuid().ToString("N").Where(char.IsLetter).Take(3).Select(char.ToUpperInvariant).ToArray()).PadRight(3, 'Q');
        TestModuleBranchValidator.Enabled = false;
        (await SendAsync(new AddEditBranchCommand { Code = TestBranches.NewCode(), Name = name })).Succeeded.Should().BeTrue();
        TestModuleBranchValidator.Enabled = true;
        await FluentActions.Awaiting(() => SendAsync(new AddEditBranchCommand { Code = code, Name = name }))
            .Should().ThrowAsync<FluentValidation.ValidationException>().WithMessage("*" + TestModuleBranchValidator.NameMessage + "*");
    }
}
