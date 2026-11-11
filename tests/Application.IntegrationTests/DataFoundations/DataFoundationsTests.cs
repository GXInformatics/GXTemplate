using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Branches;
using CleanArchitecture.Blazor.Application.Common.Concurrency;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.ExceptionHandlers;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Models;
using CleanArchitecture.Blazor.Application.Common.Numerics;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Application.Common.Transactions;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Transactions;
using CleanArchitecture.Blazor.Application.IntegrationTests.Transactions;
using CleanArchitecture.Blazor.Application.Pipeline;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Rules;
using FluentAssertions;
using Mediator;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NUnit.Framework;
using static CleanArchitecture.Blazor.Application.IntegrationTests.DataFoundations.ModelHarness;

namespace CleanArchitecture.Blazor.Application.IntegrationTests.DataFoundations;

/// <summary>A branch command, run through the real behaviours by the tests.</summary>
public sealed class BranchCmd(int branchId) : IRequest<Result<int>>, IBranchCommand, ITransactionalRequest
{
    public int BranchId { get; } = branchId;
}

/// <summary>
/// Pass 59 plan §5, on the test model (a ledger, a posted document, branch rows and a transfer). Rows
/// that use the template's own PicklistSet are in <see cref="PicklistVersionTests"/>.
/// </summary>
[NonParallelizable]
public class DataFoundationsTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    [OneTimeSetUp]
    public async Task Ready() => await RootAsync();

    // ---- helpers ------------------------------------------------------------------------------

    private static async Task SeedAsync(params object[] entities)
    {
        using var _ = Accessor.Push(ModelHarness.System);
        var (scope, db) = await ContextAsync();
        using (scope)
        await using (db)
        {
            db.AddRange(entities);
            await db.SaveChangesAsync();
        }
    }

    private static async Task<T> InAsync<T>(UserContext? user, Func<TestModelDbContext, Task<T>> body)
    {
        using var _ = Accessor.Push(user);
        var (scope, db) = await ContextAsync();
        using (scope)
        await using (db)
        {
            return await body(db);
        }
    }

    private static Task InAsync(UserContext? user, Func<TestModelDbContext, Task> body) =>
        InAsync<int>(user, async db => { await body(db); return 0; });

    private static string Tag() => "t" + Guid.NewGuid().ToString("N")[..10];

    private static async Task<TestPostedDocument> PostedDocumentAsync(int lines = 1, bool posted = true)
    {
        var doc = new TestPostedDocument { Note = "original", PostedAt = posted ? DateTime.UtcNow : null };
        for (var i = 0; i < lines; i++) doc.Lines.Add(new TestPostedLine { Quantity = 2m, UnitCost = 1.5m });
        await SeedAsync(doc);
        return doc;
    }

    private static Task<Result<int>> RunBranchAsync(IServiceProvider services, int branchId, Func<CancellationToken, ValueTask<Result<int>>> handler)
    {
        var behaviour = ActivatorUtilities.CreateInstance<BranchAccessBehaviour<BranchCmd, Result<int>>>(services);
        return behaviour.Handle(new BranchCmd(branchId), (_, ct) => handler(ct), CancellationToken.None).AsTask();
    }

    // ---- 4: precision -------------------------------------------------------------------------

    [Test]
    public async Task T04_AValueWithExcessScaleIsRefused_AndTheRoundedValueSaves()
    {
        var act = () => SeedAsync(new TestLedgerRow { Ref = Tag(), Amount = 1.005m });
        (await act.Should().ThrowAsync<DataRuleViolationException>())
            .Which.Message.Should().Contain("Amount").And.Contain("[Money]");

        var row = new TestLedgerRow { Ref = Tag(), Amount = GxRound.Money(1.005m) };
        await SeedAsync(row);
        row.Amount.Should().Be(1.01m);
        (await InAsync(ModelHarness.System, db => db.TestLedgerRows.SingleAsync(r => r.Id == row.Id))).Amount.Should().Be(1.01m);
    }

    // ---- 10: ForUpdate and xmin ---------------------------------------------------------------

    [Test]
    public async Task T10_ForUpdateDoesNotChangeXmin_AndAVersionedEditWaitsThenConflicts()
    {
        var doc = await PostedDocumentAsync(lines: 0, posted: false);
        using var _ = Accessor.Push(ModelHarness.System);
        var services = await RootAsync();

        uint VersionNow()
        {
            using var s = services.CreateScope();
            var d = (TestModelDbContext)s.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext();
            return d.TestPostedDocuments.AsNoTracking().Single(x => x.Id == doc.Id).Version;
        }

        var before = VersionNow();
        var (scopeA, a) = await ContextAsync();
        var (scopeB, b) = await ContextAsync();
        try
        {
            // B loads first: this is the version "the user loaded".
            var bDoc = await b.TestPostedDocuments.SingleAsync(x => x.Id == doc.Id);

            await using var tx = await a.Database.BeginTransactionAsync();
            var aDoc = await a.TestPostedDocuments.Where(x => x.Id == doc.Id).ForUpdate().FirstAsync();
            VersionNow().Should().Be(before, "a row lock records the locker in xmax; xmin is unchanged");

            aDoc.Note = "changed under the lock";
            await a.SaveChangesAsync();

            bDoc.Note = "B's edit";
            var bSave = b.SaveChangesAsync();
            (await Task.WhenAny(bSave, Task.Delay(500))).Should().NotBe(bSave, "B's versioned UPDATE waits for A's lock");

            await tx.CommitAsync();
            var act = async () => await bSave.WaitAsync(Guard);
            await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        }
        finally
        {
            await a.DisposeAsync(); scopeA.Dispose();
            await b.DisposeAsync(); scopeB.Dispose();
        }
    }

    // ---- 12: append-only ----------------------------------------------------------------------

    [Test]
    public async Task T12_AnAppendOnlyRowIsNeverModifiedOrDeleted()
    {
        var row = new TestLedgerRow { Ref = Tag(), Amount = 10m };
        await SeedAsync(row);

        var modify = () => InAsync(ModelHarness.System, async db =>
        {
            (await db.TestLedgerRows.SingleAsync(r => r.Id == row.Id)).Amount = 11m;
            await db.SaveChangesAsync();
        });
        (await modify.Should().ThrowAsync<DataRuleViolationException>()).Which.Message.Should().Contain("append-only");

        var delete = () => InAsync(ModelHarness.System, async db =>
        {
            db.TestLedgerRows.Remove(await db.TestLedgerRows.SingleAsync(r => r.Id == row.Id));
            await db.SaveChangesAsync();
        });
        await delete.Should().ThrowAsync<DataRuleViolationException>();

        (await InAsync(ModelHarness.System, db => db.TestLedgerRows.SingleAsync(r => r.Id == row.Id))).Amount.Should().Be(10m);
    }

    // ---- 13: posted header --------------------------------------------------------------------

    [Test]
    public async Task T13_APostedDocumentChangesOnlyInAllowedProperties_AndIsNeverDeletedOrUnposted()
    {
        var doc = await PostedDocumentAsync(lines: 0);

        Task Change(Action<TestPostedDocument> change) => InAsync(ModelHarness.System, async db =>
        {
            change(await db.TestPostedDocuments.SingleAsync(d => d.Id == doc.Id));
            await db.SaveChangesAsync();
        });

        await FluentActions.Awaiting(() => Change(d => d.Note = "edited")).Should().ThrowAsync<DataRuleViolationException>()
            .WithMessage("*posted*Note*");
        await FluentActions.Awaiting(() => Change(d => d.PostedAt = null)).Should().ThrowAsync<DataRuleViolationException>();

        // A reversal: status and link, which are [AllowedAfterPosting].
        var reversal = Guid.CreateVersion7();
        await Change(d => { d.Status = "Reversed"; d.ReversedById = reversal; });

        var delete = () => InAsync(ModelHarness.System, async db =>
        {
            db.TestPostedDocuments.Remove(await db.TestPostedDocuments.SingleAsync(d => d.Id == doc.Id));
            await db.SaveChangesAsync();
        });
        await delete.Should().ThrowAsync<DataRuleViolationException>().WithMessage("*posted and cannot be deleted*");

        var stored = await InAsync(ModelHarness.System, db => db.TestPostedDocuments.AsNoTracking().SingleAsync(d => d.Id == doc.Id));
        stored.Status.Should().Be("Reversed");
        stored.Note.Should().Be("original");
    }

    // ---- 14 and 32: lines ---------------------------------------------------------------------

    [Test]
    public async Task T14_ALineCannotBeAddedEditedOrDeletedOnAPostedDocument_AndADraftLineTouchesTheHeader()
    {
        var doc = await PostedDocumentAsync(lines: 1);

        await FluentActions.Awaiting(() => InAsync(ModelHarness.System, async db =>
        {
            var header = await db.TestPostedDocuments.Include(d => d.Lines).SingleAsync(d => d.Id == doc.Id);
            header.Lines.Add(new TestPostedLine { Quantity = 1m });
            await db.SaveChangesAsync();
        })).Should().ThrowAsync<DataRuleViolationException>().WithMessage("*cannot be added to*posted*");

        await FluentActions.Awaiting(() => InAsync(ModelHarness.System, async db =>
        {
            var header = await db.TestPostedDocuments.Include(d => d.Lines).SingleAsync(d => d.Id == doc.Id);
            header.Lines[0].Quantity = 9m;
            await db.SaveChangesAsync();
        })).Should().ThrowAsync<DataRuleViolationException>().WithMessage("*Quantity*");

        await FluentActions.Awaiting(() => InAsync(ModelHarness.System, async db =>
        {
            var header = await db.TestPostedDocuments.Include(d => d.Lines).SingleAsync(d => d.Id == doc.Id);
            db.TestPostedLines.Remove(header.Lines[0]);
            await db.SaveChangesAsync();
        })).Should().ThrowAsync<DataRuleViolationException>().WithMessage("*cannot be deleted from*");

        // A draft: adding a line works, and the header's version changes with it (the touch).
        var draft = await PostedDocumentAsync(lines: 0, posted: false);
        await InAsync(ModelHarness.System, async db =>
        {
            var header = await db.TestPostedDocuments.SingleAsync(d => d.Id == draft.Id);
            var before = header.Version;
            db.TestPostedLines.Add(new TestPostedLine { Header = header, Quantity = 1m });
            await db.SaveChangesAsync();
            header.Version.Should().NotBe(before, "a line change is saved with the header and its version check");
        });

        // A line whose header is not loaded is refused rather than slipping past the touch.
        await FluentActions.Awaiting(() => InAsync(ModelHarness.System, async db =>
        {
            db.TestPostedLines.Add(new TestPostedLine { HeaderId = draft.Id, Quantity = 1m });
            await db.SaveChangesAsync();
        })).Should().ThrowAsync<DataRuleViolationException>().WithMessage("*header is not loaded*");
    }

    [Test]
    public async Task T32_B2_OnAPostedDocument_AnAllowedLinePropertyChanges_AndNoOtherDoes()
    {
        var doc = await PostedDocumentAsync(lines: 1);

        await InAsync(ModelHarness.System, async db =>
        {
            var header = await db.TestPostedDocuments.Include(d => d.Lines).SingleAsync(d => d.Id == doc.Id);
            header.Lines[0].ReturnedQuantity = 1m;
            await db.SaveChangesAsync();
        });

        await FluentActions.Awaiting(() => InAsync(ModelHarness.System, async db =>
        {
            var header = await db.TestPostedDocuments.Include(d => d.Lines).SingleAsync(d => d.Id == doc.Id);
            header.Lines[0].UnitCost = 2m;
            await db.SaveChangesAsync();
        })).Should().ThrowAsync<DataRuleViolationException>().WithMessage("*UnitCost*");

        (await InAsync(ModelHarness.System, db => db.TestPostedLines.AsNoTracking().SingleAsync(l => l.HeaderId == doc.Id)))
            .ReturnedQuantity.Should().Be(1m);
    }

    // ---- 15: the line/post race ---------------------------------------------------------------

    [Test]
    public async Task T15_ALineAddedWhileTheDocumentIsPosted_EitherConflictsOrIsPosted_NeverSlipsIn()
    {
        var doc = await PostedDocumentAsync(lines: 1, posted: false);
        using var _ = Accessor.Push(ModelHarness.System);
        var (scopeP, poster) = await ContextAsync();
        var (scopeL, editor) = await ContextAsync();
        try
        {
            // The editor loaded the draft (its form) before the post began.
            var editorHeader = await editor.TestPostedDocuments.SingleAsync(d => d.Id == doc.Id);

            await using var tx = await poster.Database.BeginTransactionAsync();
            var header = await poster.TestPostedDocuments.Where(d => d.Id == doc.Id).ForUpdate().FirstAsync();
            var linesPosted = await poster.TestPostedLines.CountAsync(l => l.HeaderId == doc.Id);
            header.PostedAt = DateTime.UtcNow;
            await poster.SaveChangesAsync();

            editor.TestPostedLines.Add(new TestPostedLine { Header = editorHeader, Quantity = 5m });
            var addLine = editor.SaveChangesAsync();
            (await Task.WhenAny(addLine, Task.Delay(500))).Should().NotBe(addLine, "the line save carries the header update, which waits for the post");

            await tx.CommitAsync();
            await FluentActions.Awaiting(() => addLine.WaitAsync(Guard)).Should().ThrowAsync<DbUpdateConcurrencyException>();

            var stored = await InAsync(ModelHarness.System, db => db.TestPostedLines.CountAsync(l => l.HeaderId == doc.Id));
            stored.Should().Be(linesPosted, "the posted document holds exactly the lines it posted");
        }
        finally
        {
            await poster.DisposeAsync(); scopeP.Dispose();
            await editor.DisposeAsync(); scopeL.Dispose();
        }
    }

    // ---- 16 and 33: the unit-of-work exemption ------------------------------------------------

    [Test]
    public async Task T16_T33_AHeaderPostedInTheUnitOfWork_AndItsLines_StayEditableUntilCommit()
    {
        var doc = await PostedDocumentAsync(lines: 1, posted: false);
        using var _ = Accessor.Push(ModelHarness.System);
        var services = await RootAsync();
        using var scope = services.CreateScope();

        var result = await TransactionKit.RunAsync(scope.ServiceProvider, new TxCommand(), async ct =>
        {
            await using var db = (TestModelDbContext)await TransactionKit.ContextAsync(scope.ServiceProvider, ct);
            var header = await db.TestPostedDocuments.Include(d => d.Lines).SingleAsync(d => d.Id == doc.Id, ct);
            header.PostedAt = DateTime.UtcNow;
            // A posting handler finishes the posting: records the unit cost on the line and a note on the header.
            header.AddDomainEvent(new ScriptedPostingEvent("cost", async (sp, c) =>
            {
                await using var inner = (TestModelDbContext)await TransactionKit.ContextAsync(sp, c);
                var line = await inner.TestPostedLines.SingleAsync(l => l.HeaderId == doc.Id, c);
                line.UnitCost = 3.25m;                                 // B3: a line of the exempt header
                (await inner.TestPostedDocuments.SingleAsync(d => d.Id == doc.Id, c)).Note = "costed";
                await inner.SaveChangesAsync(c);
            }));
            await db.SaveChangesAsync(ct);
            return Result<int>.Success(1);
        });
        result.Succeeded.Should().BeTrue(result.ErrorMessage);

        var stored = await InAsync(ModelHarness.System, db => db.TestPostedDocuments.Include(d => d.Lines).AsNoTracking().SingleAsync(d => d.Id == doc.Id));
        stored.Note.Should().Be("costed");
        stored.Lines.Single().UnitCost.Should().Be(3.25m);

        // After the commit the same changes are refused.
        await FluentActions.Awaiting(() => InAsync(ModelHarness.System, async db =>
        {
            (await db.TestPostedLines.Include(l => l.Header).SingleAsync(l => l.HeaderId == doc.Id)).UnitCost = 4m;
            await db.SaveChangesAsync();
        })).Should().ThrowAsync<DataRuleViolationException>();
        await FluentActions.Awaiting(() => InAsync(ModelHarness.System, async db =>
        {
            (await db.TestPostedDocuments.SingleAsync(d => d.Id == doc.Id)).Note = "later";
            await db.SaveChangesAsync();
        })).Should().ThrowAsync<DataRuleViolationException>();
    }

    // ---- 17: bulk operations ------------------------------------------------------------------

    [Test]
    public async Task T17_BulkWritesToGuardedTablesAreRefused_AndToOtherTablesWork()
    {
        var branchId = await CreateBranchAsync();   // audited, so created outside the fake system principal
        using var _ = Accessor.Push(ModelHarness.System);
        var (scope, db) = await ContextAsync();
        using (scope)
        await using (db)
        {
            await FluentActions.Awaiting(() => db.TestLedgerRows.ExecuteUpdateAsync(s => s.SetProperty(r => r.Amount, 0m)))
                .Should().ThrowAsync<DataRuleViolationException>().WithMessage("*TBL_TEST_LEDGER_ROW*");
            await FluentActions.Awaiting(() => db.TestPostedDocuments.Where(d => d.Note == "none").ExecuteDeleteAsync())
                .Should().ThrowAsync<DataRuleViolationException>();
            await FluentActions.Awaiting(() => db.TestBranchRows.Where(r => r.Name == "none").ExecuteDeleteAsync())
                .Should().ThrowAsync<DataRuleViolationException>();
            await FluentActions.Awaiting(() => db.Database.ExecuteSqlRawAsync(
                    "UPDATE tdf.\"TBL_TEST_LEDGER_ROW\" SET \"Amount\" = 0 WHERE false"))
                .Should().ThrowAsync<DataRuleViolationException>();

            // Not guarded: works.
            (await db.Branches.Where(b => b.Id == branchId).ExecuteUpdateAsync(s => s.SetProperty(b => b.Name, "renamed in bulk"))).Should().Be(1);
        }
    }

    // ---- 19: triggers -------------------------------------------------------------------------

    [Test]
    public async Task T19_TheDatabaseRefusesUpdateDeleteAndTruncateOnALedger_AndAMissingTriggerFailsTheCheck()
    {
        var row = new TestLedgerRow { Ref = Tag(), Amount = 5m };
        await SeedAsync(row);
        using var _ = Accessor.Push(ModelHarness.System);
        var (scope, db) = await ContextAsync();
        using (scope)
        await using (db)
        {
            var table = "tdf.\"TBL_TEST_LEDGER_ROW\""; // (P60) the test module's schema
            await using var connection = new NpgsqlConnection(db.Database.GetConnectionString());
            await connection.OpenAsync();

            foreach (var sql in new[] { $"UPDATE {table} SET \"Amount\" = 1 WHERE \"Id\" = {row.Id}", $"DELETE FROM {table} WHERE \"Id\" = {row.Id}", $"TRUNCATE {table}" })
            {
                var act = async () => { await using var c = new NpgsqlCommand(sql, connection); await c.ExecuteNonQueryAsync(); };
                (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.RestrictViolation, sql);
            }

            (await DataModelGuard.FindTablesMissingTriggersAsync(db)).Should().BeEmpty();
            await using (var disable = new NpgsqlCommand($"ALTER TABLE {table} DISABLE TRIGGER {DataModelGuard.TruncateTrigger}", connection))
                await disable.ExecuteNonQueryAsync();
            try
            {
                (await DataModelGuard.FindTablesMissingTriggersAsync(db)).Should().ContainSingle().Which.Should().Contain("TBL_TEST_LEDGER_ROW");
                await FluentActions.Awaiting(() => DataModelGuard.AssertAppendOnlyTriggersAsync(db)).Should().ThrowAsync<InvalidOperationException>();
            }
            finally
            {
                await using var enable = new NpgsqlCommand($"ALTER TABLE {table} ENABLE TRIGGER {DataModelGuard.TruncateTrigger}", connection);
                await enable.ExecuteNonQueryAsync();
            }
        }
    }

    // ---- 21, 22, 28: reads ---------------------------------------------------------------------

    private static async Task<(int B1, int B2, int B3, string Tag)> ThreeBranchRowsAsync()
    {
        var tag = Tag();
        int b1 = await CreateBranchAsync(), b2 = await CreateBranchAsync(), b3 = await CreateBranchAsync();
        await SeedAsync(new TestBranchRow { BranchId = b1, Name = tag }, new TestBranchRow { BranchId = b2, Name = tag }, new TestBranchRow { BranchId = b3, Name = tag });
        return (b1, b2, b3, tag);
    }

    [Test]
    public async Task T21_AUserReadsOnlyTheirReadSet_AndNoPrincipalReadsNothing()
    {
        var (b1, b2, b3, tag) = await ThreeBranchRowsAsync();
        var reader = Reader("reader", Reads(b1, b2));

        (await InAsync(reader, db => db.TestBranchRows.CountAsync(r => r.Name == tag))).Should().Be(2);
        (await InAsync(reader, db => db.TestBranchRows.Where(r => r.Name == tag).Select(r => r.BranchId).OrderBy(x => x).ToListAsync()))
            .Should().Equal(b1, b2);
        var b3Id = await InAsync(ModelHarness.System, db => db.TestBranchRows.Where(r => r.Name == tag && r.BranchId == b3).Select(r => r.Id).SingleAsync());
        (await InAsync(reader, db => db.TestBranchRows.FindAsync(b3Id).AsTask())).Should().BeNull();

        (await InAsync(null, db => db.TestBranchRows.CountAsync(r => r.Name == tag))).Should().Be(0, "unknown is treated as no access");
        (await InAsync(Reader("nobody", null!), db => db.TestBranchRows.CountAsync(r => r.Name == tag))).Should().Be(0);
    }

    [Test]
    public async Task T22_AViewAllUserReadsEverything_ButACommandForAnUnassignedBranchIsRefused()
    {
        var (b1, b2, _, tag) = await ThreeBranchRowsAsync();
        var userId = await CreateUserAsync([b1], Permissions.Branches.ViewAll);
        var user = Reader(userId, ReadsAll(b1));

        (await InAsync(user, db => db.TestBranchRows.CountAsync(r => r.Name == tag))).Should().Be(3);

        using var _ = Accessor.Push(user);
        var services = await RootAsync();
        using var scope = services.CreateScope();
        var ran = false;
        var act = () => RunBranchAsync(scope.ServiceProvider, b2, _ => { ran = true; return ValueTask.FromResult(Result<int>.Success(1)); });
        await act.Should().ThrowAsync<ForbiddenAccessException>().WithMessage("You are not assigned to branch*");
        ran.Should().BeFalse("nothing runs, so nothing is written");

        (await RunBranchAsync(scope.ServiceProvider, b1, _ => ValueTask.FromResult(Result<int>.Success(1)))).Succeeded.Should().BeTrue();
    }

    [Test]
    public async Task T28_TheSystemContextReadsAndWritesEveryBranch()
    {
        var (b1, b2, b3, tag) = await ThreeBranchRowsAsync();
        (await InAsync(ModelHarness.System, db => db.TestBranchRows.CountAsync(r => r.Name == tag))).Should().Be(3);
        await SeedAsync(new TestBranchRow { BranchId = b1, Name = tag }, new TestBranchRow { BranchId = b3, Name = tag });
        (await InAsync(ModelHarness.System, db => db.TestBranchRows.CountAsync(r => r.Name == tag && r.BranchId != b2))).Should().Be(4);
    }

    // ---- 23: writes need a grant --------------------------------------------------------------

    [Test]
    public async Task T23_ABranchRowSavedWithoutAGrant_OrForAnotherBranch_OrMoved_IsRefused()
    {
        var (b1, b2, _, tag) = await ThreeBranchRowsAsync();
        var user = Reader("writer", Reads(b1, b2));

        await FluentActions.Awaiting(() => InAsync(user, async db =>
        {
            db.TestBranchRows.Add(new TestBranchRow { BranchId = b1, Name = tag });
            await db.SaveChangesAsync();
        })).Should().ThrowAsync<DataRuleViolationException>().WithMessage("*outside a branch command*IBranchCommand*");

        using (BranchGrant.Begin(b1))
        {
            await FluentActions.Awaiting(() => InAsync(user, async db =>
            {
                db.TestBranchRows.Add(new TestBranchRow { BranchId = b2, Name = tag });
                await db.SaveChangesAsync();
            })).Should().ThrowAsync<DataRuleViolationException>().WithMessage($"*belongs to branch {b2}*");

            await FluentActions.Awaiting(() => InAsync(user, async db =>
            {
                (await db.TestBranchRows.FirstAsync(r => r.Name == tag && r.BranchId == b1)).BranchId = b2;
                await db.SaveChangesAsync();
            })).Should().ThrowAsync<DataRuleViolationException>().WithMessage("*never moved to another branch*");

            await InAsync(user, async db =>
            {
                db.TestBranchRows.Add(new TestBranchRow { BranchId = b1, Name = tag });
                await db.SaveChangesAsync();
            });
        }

        BranchGrant.Current.Should().BeNull("the grant ends with its command");
    }

    // ---- 24: two-branch documents -------------------------------------------------------------

    [Test]
    public async Task T24_ATransferIsVisibleToBothBranchesAndNoThird_AndExtendsTheGrantToItsOtherBranch()
    {
        int b1 = await CreateBranchAsync(), b2 = await CreateBranchAsync(), b3 = await CreateBranchAsync();
        var tag = Tag();
        var transfer = new TestTransfer { FromBranchId = b1, ToBranchId = b2, Note = tag };
        await SeedAsync(transfer);

        (await InAsync(Reader("r1", Reads(b1)), db => db.TestTransfers.CountAsync(t => t.Note == tag))).Should().Be(1);
        (await InAsync(Reader("r2", Reads(b2)), db => db.TestTransfers.CountAsync(t => t.Note == tag))).Should().Be(1);
        (await InAsync(Reader("r3", Reads(b3)), db => db.TestTransfers.CountAsync(t => t.Note == tag))).Should().Be(0);

        var sender = Reader("sender", Reads(b1));
        using (BranchGrant.Begin(b1))
        {
            // Before the transfer is saved, the receiving branch is not granted.
            await FluentActions.Awaiting(() => InAsync(sender, async db =>
            {
                db.TestBranchRows.Add(new TestBranchRow { BranchId = b2, Name = tag });
                await db.SaveChangesAsync();
            })).Should().ThrowAsync<DataRuleViolationException>();

            // Saving the transfer extends the grant to b2 (its rows derive from the transfer); never to b3.
            await InAsync(sender, async db =>
            {
                db.TestTransfers.Add(new TestTransfer { FromBranchId = b1, ToBranchId = b2, Note = tag + "-2" });
                await db.SaveChangesAsync();
                db.TestBranchRows.Add(new TestBranchRow { BranchId = b2, Name = tag });
                await db.SaveChangesAsync();
            });
            await FluentActions.Awaiting(() => InAsync(sender, async db =>
            {
                db.TestBranchRows.Add(new TestBranchRow { BranchId = b3, Name = tag });
                await db.SaveChangesAsync();
            })).Should().ThrowAsync<DataRuleViolationException>();
        }

        // A transfer that involves none of the granted branches is refused.
        using (BranchGrant.Begin(b3))
        {
            await FluentActions.Awaiting(() => InAsync(Reader("r3", Reads(b3)), async db =>
            {
                db.TestTransfers.Add(new TestTransfer { FromBranchId = b1, ToBranchId = b2, Note = tag });
                await db.SaveChangesAsync();
            })).Should().ThrowAsync<DataRuleViolationException>();
        }
    }

    // ---- 26: composition with pass 58 ---------------------------------------------------------

    [Test]
    public async Task T26_ForUpdateOnABranchFilteredEntityHasNoJoin_AndARetriedBranchCommandKeepsItsGrant()
    {
        var (b1, _, _, tag) = await ThreeBranchRowsAsync();
        var userId = await CreateUserAsync([b1]);
        var user = Reader(userId, Reads(b1));

        await InAsync(user, async db =>
        {
            var sql = db.TestBranchRows.Where(r => r.Name == tag).OrderBy(r => r.Id).ForUpdate().ToQueryString();
            sql.Should().NotContainEquivalentOf("JOIN");
            await using var tx = await db.Database.BeginTransactionAsync();
            (await db.TestBranchRows.Where(r => r.Name == tag).OrderBy(r => r.Id).ForUpdate().ToListAsync()).Should().ContainSingle();
        });

        using var _ = Accessor.Push(user);
        var services = await RootAsync();
        using var scope = services.CreateScope();
        var attempts = 0;
        var result = await RunBranchAsync(scope.ServiceProvider, b1, async _ =>
            await TransactionKit.RunAsync(scope.ServiceProvider, new BranchCmd(b1), async ct =>
            {
                attempts++;
                await using var db = (TestModelDbContext)await TransactionKit.ContextAsync(scope.ServiceProvider, ct);
                db.TestBranchRows.Add(new TestBranchRow { BranchId = b1, Name = tag + "-retry" });
                await db.SaveChangesAsync(ct);
                if (attempts == 1) throw new PostgresException("deadlock detected", "ERROR", "ERROR", PostgresErrorCodes.DeadlockDetected);
                return Result<int>.Success(1);
            }));

        result.Succeeded.Should().BeTrue(result.ErrorMessage);
        attempts.Should().Be(2);
        (await InAsync(ModelHarness.System, db => db.TestBranchRows.CountAsync(r => r.Name == tag + "-retry"))).Should().Be(1);
    }

    // ---- 31: B1 write-all ---------------------------------------------------------------------

    [Test]
    public async Task T31_B1_WriteAllWritesToAnyActiveBranchAndNoInactiveOne_AndRevokingItRefusesTheNextWrite()
    {
        int active = await CreateBranchAsync(), inactive = await CreateBranchAsync(active: false);
        var userId = await CreateUserAsync([], Permissions.Branches.WriteAll);
        using var _ = Accessor.Push(Reader(userId, new BranchAccess(false, [], [], true, null)));
        var services = await RootAsync();
        using var scope = services.CreateScope();
        Task<Result<int>> Write(int branch) => RunBranchAsync(scope.ServiceProvider, branch, _ => ValueTask.FromResult(Result<int>.Success(branch)));

        (await Write(active)).Succeeded.Should().BeTrue();
        await FluentActions.Awaiting(() => Write(inactive)).Should().ThrowAsync<ForbiddenAccessException>();

        // Revoke it from the user's role: the very next write is refused (read fresh on each send).
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var role = await roleManager.FindByNameAsync((await userManager.GetRolesAsync((await userManager.FindByIdAsync(userId))!)).Single());
        await roleManager.RemoveClaimAsync(role!, new Claim(ApplicationClaimTypes.Permission, Permissions.Branches.WriteAll));

        await FluentActions.Awaiting(() => Write(active)).Should().ThrowAsync<ForbiddenAccessException>().WithMessage("You are not assigned to branch*");
    }

    // ---- 35: B5 reading across branches -------------------------------------------------------

    [Test]
    public async Task T35_B5_AcrossBranchesNeedsTheNamedPermission_AndOrdinaryQueriesStayFiltered()
    {
        var (b1, _, _, tag) = await ThreeBranchRowsAsync();
        // A module's narrower permission, standing in for "see a part's stock in other branches".
        const string StockAcrossBranches = Permissions.PicklistSets.View;
        var holder = await CreateUserAsync([b1], StockAcrossBranches);
        var other = await CreateUserAsync([b1]);
        var services = await RootAsync();

        async Task<int> Across(string userId) => await InAsync(Reader(userId, Reads(b1)), async db =>
        {
            using var scope = services.CreateScope();
            var branches = scope.ServiceProvider.GetRequiredService<IBranchQueryScope>();
            var query = await branches.AcrossBranchesAsync(db.TestBranchRows.Where(r => r.Name == tag), StockAcrossBranches);
            return await query.CountAsync();
        });

        (await Across(holder)).Should().Be(3);
        await FluentActions.Awaiting(() => Across(other)).Should().ThrowAsync<ForbiddenAccessException>();
        (await InAsync(Reader(holder, Reads(b1)), db => db.TestBranchRows.CountAsync(r => r.Name == tag))).Should().Be(1,
            "the helper removes the filter from one query, not from the user");
    }
}
