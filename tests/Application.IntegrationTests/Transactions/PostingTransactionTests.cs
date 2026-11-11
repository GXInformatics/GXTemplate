#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.ExceptionHandlers;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Transactions;
using CleanArchitecture.Blazor.Application.Common.Models;
using CleanArchitecture.Blazor.Application.Common.Transactions;
using CleanArchitecture.Blazor.Application.Pipeline;
using CleanArchitecture.Blazor.Domain.Entities;
using FluentAssertions;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.IntegrationTests.Transactions;

using static Testing;
using static TransactionKit;

/// <summary>
/// Pass 58: the unit of work, posting events, nesting and retry, against the real interceptors and
/// PostgreSQL. Numbers are the rows of the table in docs/passes/pass58-transactions-plan.md §5.
/// </summary>
public class PostingTransactionTests : TestBase
{
    /// <summary>Every cross-task wait is bounded: a failed partner must fail the test, not hang the run.</summary>
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    [SetUp]
    public void ResetKit() => Reset();

    // ---- 1 -------------------------------------------------------------------------------------

    [Test]
    public async Task T01_APostingHandlerThatThrows_RollsBackEverything_AuditRowsIncluded()
    {
        using var scope = CreateScope();
        var auditBefore = await AuditRowsAsync();

        var act = async () => await RunAsync(scope.ServiceProvider, new TxCommand(), async ct =>
        {
            await using var db = await ContextAsync(scope.ServiceProvider, ct);
            db.Tenants.Add(Header("t01"));
            db.PicklistSets.Add(Line("t01-line", new ScriptedPostingEvent("stock", async (sp, c) =>
            {
                await using var inner = await ContextAsync(sp, c);
                inner.PicklistSets.Add(Line("t01-movement"));
                await inner.SaveChangesAsync(c);
                throw new InvalidOperationException("the posting handler failed");
            })));
            await db.SaveChangesAsync(ct);
            return Result<int>.Success(1);
        });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("the posting handler failed");
        (await TenantsAsync("t01")).Should().Be(0);
        (await LinesAsync("t01")).Should().Be(0);
        (await AuditRowsAsync()).Should().Be(auditBefore, "the audit rows were written in the same transaction");
    }

    // ---- 2 -------------------------------------------------------------------------------------

    [Test]
    public async Task T02_AFailureResult_RollsBack_AndPublishesNothing()
    {
        using var scope = CreateScope();
        var publisher = new RecordingPublisher();

        var result = await RunAsync(scope.ServiceProvider, new TxCommand(), async ct =>
        {
            await using var db = await ContextAsync(scope.ServiceProvider, ct);
            db.Tenants.Add(Header("t02"));
            db.PicklistSets.Add(Line("t02-line", new AfterCommitProbe("t02"), new ScriptedPostingEvent("never")));
            await db.SaveChangesAsync(ct);
            return Result<int>.Failure("the handler said no");
        }, publisher);

        result.Succeeded.Should().BeFalse();
        result.Errors.Should().Equal("the handler said no");
        (await TenantsAsync("t02")).Should().Be(0);
        (await LinesAsync("t02")).Should().Be(0);
        publisher.Published.Should().BeEmpty();
        ScriptedPostingEventHandler.Dispatched.Should().BeEmpty("a failed handler result stops before the posting loop");
    }

    // ---- 3 -------------------------------------------------------------------------------------

    [Test]
    public async Task T03_AfterCommitEvents_ArePublishedAfterTheCommit_WithNoUnitOfWorkActive()
    {
        using var scope = CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        bool? activeAtPublish = null;
        int? committedAtPublish = null;
        var publisher = new RecordingPublisher(async _ =>
        {
            activeAtPublish = unitOfWork.IsActive;
            committedAtPublish = await TenantsAsync("t03");
        });

        var result = await RunAsync(scope.ServiceProvider, new TxCommand(), async ct =>
        {
            await using var db = await ContextAsync(scope.ServiceProvider, ct);
            db.Tenants.Add(Header("t03"));
            db.PicklistSets.Add(Line("t03-line", new AfterCommitProbe("t03")));
            await db.SaveChangesAsync(ct);
            publisher.Published.Should().BeEmpty("nothing is published while the transaction is open");
            return Result<int>.Success(1);
        }, publisher);

        result.Succeeded.Should().BeTrue();
        publisher.Published.OfType<AfterCommitProbe>().Select(e => e.Name).Should().Equal("t03");
        activeAtPublish.Should().BeFalse();
        committedAtPublish.Should().Be(1, "another connection already sees the committed row");
    }

    // ---- 4 -------------------------------------------------------------------------------------

    [Test]
    public async Task T04_InsideAUnitOfWork_TheFactoryReturnsOneContext_AndDisposingItDoesNothing()
    {
        using var scope = CreateScope();
        IApplicationDbContext? first = null;
        IApplicationDbContext? second = null;

        var result = await RunAsync(scope.ServiceProvider, new TxCommand(), async ct =>
        {
            await using (var db = await ContextAsync(scope.ServiceProvider, ct))
            {
                first = db;
                db.Tenants.Add(Header("t04"));
                await db.SaveChangesAsync(ct);
            }

            await using var again = await ContextAsync(scope.ServiceProvider, ct);
            second = again;
            (await again.Tenants.CountAsync(t => t.Id.StartsWith("t04"), ct)).Should().Be(1, "the disposed-by-using context still works");
            return Result<int>.Success(1);
        });

        result.Succeeded.Should().BeTrue();
        ReferenceEquals(second, first).Should().BeTrue("the factory returned the unit of work's one context");
        (await TenantsAsync("t04")).Should().Be(1);

        var outside = await ContextAsync(scope.ServiceProvider);
        ReferenceEquals(outside, first).Should().BeFalse("outside the unit of work every call gets a new context");
        await outside.DisposeAsync();
    }

    // ---- 5 -------------------------------------------------------------------------------------

    [Test]
    public async Task T05_APostingEventSavedOutsideAUnitOfWork_Throws_NamingIt_AndWritesNothing()
    {
        using var scope = CreateScope();
        await using var db = await ContextAsync(scope.ServiceProvider);
        db.PicklistSets.Add(Line("t05-line", new ScriptedPostingEvent("orphan")));

        var act = async () => await db.SaveChangesAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ScriptedPostingEvent*ITransactionalRequest*");
        (await LinesAsync("t05")).Should().Be(0);
    }

    // ---- 6 -------------------------------------------------------------------------------------

    [Test]
    public async Task T06_TheSameRequestIdTwice_RunsTheHandlerOnce_AndReturnsTheSameResponse()
    {
        var requestId = Guid.NewGuid();
        var runs = 0;
        Func<IServiceProvider, Func<CancellationToken, ValueTask<Result<int>>>> handler = sp => async ct =>
        {
            Interlocked.Increment(ref runs);
            await using var db = await ContextAsync(sp, ct);
            db.Tenants.Add(Header("t06"));
            await db.SaveChangesAsync(ct);
            return Result<int>.Success(42);
        };

        using var first = CreateScope();
        var a = await RunAsync(first.ServiceProvider, new IdemCommand(requestId), handler(first.ServiceProvider));
        using var second = CreateScope();
        var b = await RunAsync(second.ServiceProvider, new IdemCommand(requestId), handler(second.ServiceProvider));

        runs.Should().Be(1);
        a.Succeeded.Should().BeTrue();
        b.Succeeded.Should().BeTrue();
        b.Data.Should().Be(42);
        (await TenantsAsync("t06")).Should().Be(1);
        (await ProcessedRequestsAsync()).Should().Be(1);
    }

    // ---- 7 -------------------------------------------------------------------------------------

    [Test]
    public async Task T07_TheSameRequestIdConcurrently_RunsOnce_TheOtherWaitsAndGetsTheSameResponse()
    {
        var requestId = Guid.NewGuid();
        var runs = 0;
        var firstIsInside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<Result<int>> Send(bool holdOpen)
        {
            using var scope = CreateScope();
            return await RunAsync(scope.ServiceProvider, new IdemCommand(requestId), async ct =>
            {
                Interlocked.Increment(ref runs);
                await using var db = await ContextAsync(scope.ServiceProvider, ct);
                db.Tenants.Add(Header("t07"));
                await db.SaveChangesAsync(ct);
                if (holdOpen)
                {
                    firstIsInside.TrySetResult();
                    await Task.Delay(500, ct); // the second request arrives while this one holds the claim
                }
                return Result<int>.Success(7);
            });
        }

        var first = Send(holdOpen: true);
        await firstIsInside.Task.WaitAsync(Guard);
        var second = Send(holdOpen: false);
        var results = await Task.WhenAll(first, second);

        runs.Should().Be(1, "the second claim waited for the first transaction and found it committed");
        results.Should().OnlyContain(r => r.Succeeded && r.Data == 7);
        (await TenantsAsync("t07")).Should().Be(1);
    }

    // ---- 8 -------------------------------------------------------------------------------------

    [Test]
    public async Task T08_AfterARolledBackAttempt_TheSameRequestIdRunsAgain_ButAnotherUserIsRefused()
    {
        var requestId = Guid.NewGuid();
        using (var scope = CreateScope())
        {
            var failed = await RunAsync(scope.ServiceProvider, new IdemCommand(requestId), _ =>
                ValueTask.FromResult(Result<int>.Failure("first attempt failed")));
            failed.Succeeded.Should().BeFalse();
        }
        (await ProcessedRequestsAsync()).Should().Be(0, "the claim rolled back with the attempt");

        using (var scope = CreateScope())
        {
            var retried = await RunAsync(scope.ServiceProvider, new IdemCommand(requestId), async ct =>
            {
                await using var db = await ContextAsync(scope.ServiceProvider, ct);
                db.Tenants.Add(Header("t08"));
                await db.SaveChangesAsync(ct);
                return Result<int>.Success(8);
            });
            retried.Succeeded.Should().BeTrue();
            retried.Data.Should().Be(8);
        }

        using (var scope = CreateScope())
        {
            var otherType = await RunAsync(scope.ServiceProvider, new OtherIdemCommand(requestId), _ =>
                throw new AssertionException("the handler must not run for a refused id"));
            otherType.Succeeded.Should().BeFalse("the same user, but a different request type");
            otherType.Errors.Should().Equal(TransactionBehaviour<OtherIdemCommand, Result<int>>.RequestIdReusedMessage);
        }

        await RunAsUserAsync("t08-other", "Password123!", []);
        using (var scope = CreateScope())
        {
            var reused = await RunAsync(scope.ServiceProvider, new IdemCommand(requestId), _ =>
                throw new AssertionException("the handler must not run for a refused id"));
            reused.Succeeded.Should().BeFalse("the same request type, but a different user");
            reused.Errors.Should().Equal(TransactionBehaviour<IdemCommand, Result<int>>.RequestIdReusedMessage);
        }
        (await TenantsAsync("t08")).Should().Be(1);
    }

    // ---- 9 -------------------------------------------------------------------------------------

    [Test]
    public async Task T09_ConcurrentAllocations_AreGapFree_AndARolledBackNumberIsReused()
    {
        async Task<long> Allocate(bool rollBack)
        {
            using var scope = CreateScope();
            long number = 0;
            await RunAsync(scope.ServiceProvider, new TxCommand(), async ct =>
            {
                number = await scope.ServiceProvider.GetRequiredService<IDocumentNumberAllocator>()
                    .NextAsync("GRN", branchId: 7, year: 2026, ct);
                return rollBack ? Result<int>.Failure("rolled back") : Result<int>.Success(1);
            });
            return number;
        }

        var numbers = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Allocate(rollBack: false)));
        numbers.Order().Should().Equal(Enumerable.Range(1, 50).Select(i => (long)i));

        (await Allocate(rollBack: true)).Should().Be(51);
        (await Allocate(rollBack: false)).Should().Be(51, "the rolled-back allocation left no gap");

        using var outside = CreateScope();
        var act = async () => await outside.ServiceProvider.GetRequiredService<IDocumentNumberAllocator>()
            .NextAsync("GRN", 7, 2026, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>("numbers are allocated inside the posting transaction only");
    }

    // ---- 10 ------------------------------------------------------------------------------------

    [Test]
    public async Task T10_ForUpdate_BlocksASecondTransaction()
    {
        var header = Header("t10");
        await AddAsync(header);
        var locked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var holderScope = CreateScope();
        var holder = RunAsync(holderScope.ServiceProvider, new TxCommand(), async ct =>
        {
            await using var db = await ContextAsync(holderScope.ServiceProvider, ct);
            await db.Tenants.Where(t => t.Id == header.Id).ForUpdate().SingleAsync(ct);
            locked.TrySetResult();
            await release.Task.WaitAsync(Guard, ct);
            return Result<int>.Success(1);
        }).AsTask();

        await locked.Task.WaitAsync(Guard);
        using var waiterScope = CreateScope();
        var waiter = async () => await RunAsync(waiterScope.ServiceProvider, new TxCommand(), async ct =>
        {
            await using var db = await ContextAsync(waiterScope.ServiceProvider, ct);
            await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '300ms'", ct);
            await db.Tenants.Where(t => t.Id == header.Id).ForUpdate().SingleAsync(ct);
            return Result<int>.Success(1);
        });

        try
        {
            // EF's default (non-retrying) execution strategy wraps an error Npgsql classes as transient
            // - lock_not_available is one, as are deadlocks - in an InvalidOperationException; the
            // PostgresException is its inner exception. TransactionBehaviour's retry check walks the chain.
            var thrown = await waiter.Should().ThrowAsync<Exception>();
            var postgres = thrown.Which as PostgresException ?? thrown.Which.InnerException as PostgresException;
            postgres.Should().NotBeNull("the waiter must have failed on the database, not before it");
            postgres!.SqlState.Should().Be("55P03", "lock_not_available: the row was locked by the holder");
        }
        finally
        {
            release.TrySetResult();
        }
        (await holder).Succeeded.Should().BeTrue();
    }

    [Test]
    public async Task T10_ForUpdate_IsRefused_OutsideATransaction_OnAJoin_AndWhenUnordered()
    {
        using var scope = CreateScope();

        await using (var db = await ContextAsync(scope.ServiceProvider))
        {
            var noTransaction = async () => await db.Tenants.Where(t => t.Id == "x").ForUpdate().FirstOrDefaultAsync();
            await noTransaction.Should().ThrowAsync<InvalidOperationException>().WithMessage("*needs a transaction*");
        }

        Exception? join = null, unordered = null;
        await RunAsync(scope.ServiceProvider, new TxCommand(), async ct =>
        {
            await using var db = await ContextAsync(scope.ServiceProvider, ct);
            // Document auto-includes its CreatedBy, LastModifiedBy and Tenant: a JOIN.
            try { await db.Documents.OrderBy(d => d.Id).ForUpdate().ToListAsync(ct); }
            catch (InvalidOperationException e) { join = e; }
            try { await db.Tenants.Where(t => t.Id.StartsWith("t10")).ForUpdate().ToListAsync(ct); }
            catch (InvalidOperationException e) { unordered = e; }
            return Result<int>.Success(1);
        });

        join.Should().NotBeNull();
        join!.Message.Should().Contain("joins");
        unordered.Should().NotBeNull();
        unordered!.Message.Should().Contain("lock order");
    }

    // ---- 11 ------------------------------------------------------------------------------------

    [Test]
    public async Task T11_ADeadlockFromOppositeLockOrder_IsRetried_AndBothCommandsSucceed()
    {
        var a = Header("t11a");
        var b = Header("t11b");
        await AddAsync(a);
        await AddAsync(b);
        var bothHoldTheirFirstLock = new CountdownBarrier(2);
        var attempts = 0;

        async Task<Result<int>> Post(string first, string second, string mark)
        {
            using var scope = CreateScope();
            var myAttempts = 0;
            return await RunAsync(scope.ServiceProvider, new TxCommand(), async ct =>
            {
                Interlocked.Increment(ref attempts);
                myAttempts++;
                await using var db = await ContextAsync(scope.ServiceProvider, ct);
                await db.Tenants.Where(t => t.Id == first).ForUpdate().SingleAsync(ct);
                if (myAttempts == 1) await bothHoldTheirFirstLock.SignalAndWaitAsync().WaitAsync(Guard, ct);
                var row = await db.Tenants.Where(t => t.Id == second).ForUpdate().SingleAsync(ct);
                row.Description += mark;
                await db.SaveChangesAsync(ct);
                return Result<int>.Success(1);
            });
        }

        var results = await Task.WhenAll(Post(a.Id, b.Id, "+A"), Post(b.Id, a.Id, "+B"));

        results.Should().OnlyContain(r => r.Succeeded);
        attempts.Should().Be(3, "PostgreSQL aborted one of the two with 40P01 and that one was retried once");
        var rowA = await FindAsync<Tenant>(a.Id);
        var rowB = await FindAsync<Tenant>(b.Id);
        (rowA!.Description + rowB!.Description).Should().Contain("+A").And.Contain("+B");
    }

    // ---- 14 (A1) -------------------------------------------------------------------------------

    [Test]
    public async Task T14_TwoPostsOfOneDocument_WithDifferentRequestIds_ExactlyOneSucceeds()
    {
        var header = Header("t14");
        await AddAsync(header);
        var auditBefore = await AuditRowsAsync();

        // The rule: lock the header FIRST, re-check its status inside the transaction.
        async Task<Result<int>> Post(string mark)
        {
            using var scope = CreateScope();
            return await RunAsync(scope.ServiceProvider, new IdemCommand(Guid.NewGuid()), async ct =>
            {
                await using var db = await ContextAsync(scope.ServiceProvider, ct);
                var document = await db.Tenants.Where(t => t.Id == header.Id).ForUpdate().SingleAsync(ct);
                if (document.Description != "Draft") return Result<int>.Failure($"The document is already {document.Description}.");

                await Task.Delay(200, ct); // widen the window: the other post is now queued on the lock
                document.Description = "Posted";
                db.PicklistSets.Add(Line("t14-movement-" + mark));
                await db.SaveChangesAsync(ct);
                return Result<int>.Success(1);
            });
        }

        var results = await Task.WhenAll(Post("a"), Post("b"));

        results.Count(r => r.Succeeded).Should().Be(1);
        results.Single(r => !r.Succeeded).Errors.Should().Equal("The document is already Posted.");
        (await LinesAsync("t14-movement")).Should().Be(1, "the losing post wrote nothing");
        (await ProcessedRequestsAsync()).Should().Be(1, "the losing post recorded no response");
        (await AuditRowsAsync()).Should().Be(auditBefore + 2, "one header update and one movement, from the winner only");
    }

    // ---- 15 (A2) -------------------------------------------------------------------------------

    [Test]
    public async Task T15_APostingRejection_RollsBackEverything_AndTheUserGetsItsMessage()
    {
        using var scope = CreateScope();
        var publisher = new RecordingPublisher();

        var result = await RunAsync(scope.ServiceProvider, new IdemCommand(Guid.NewGuid()), async ct =>
        {
            await using var db = await ContextAsync(scope.ServiceProvider, ct);
            db.Tenants.Add(Header("t15"));
            db.PicklistSets.Add(Line("t15-line", new AfterCommitProbe("t15"), new ScriptedPostingEvent("inventory", (_, _) =>
                throw new PostingRejectedException("Insufficient stock for item SP-001: 2 on hand, 5 requested.", "INSUFFICIENT_STOCK"))));
            await db.SaveChangesAsync(ct);
            return Result<int>.Success(1);
        }, publisher);

        result.Succeeded.Should().BeFalse();
        result.Errors.Should().Equal("Insufficient stock for item SP-001: 2 on hand, 5 requested.");
        (await TenantsAsync("t15")).Should().Be(0);
        (await LinesAsync("t15")).Should().Be(0);
        (await ProcessedRequestsAsync()).Should().Be(0, "a rejection records no idempotency response");
        publisher.Published.Should().BeEmpty();
        ScriptedPostingEventHandler.Dispatched.Should().Equal(["inventory"], "a rejection is not retried");
    }

    // ---- 16 (A3) -------------------------------------------------------------------------------

    [Test]
    public async Task T16_AChainedPostingEvent_RunsInTheSameTransaction_AndSeesTheFirstHandlersWrites()
    {
        using var scope = CreateScope();
        int? costRowsSeenByAccounting = null;

        var result = await RunAsync(scope.ServiceProvider, new TxCommand(), async ct =>
        {
            await using var db = await ContextAsync(scope.ServiceProvider, ct);
            db.Tenants.Add(Header("t16"));
            db.PicklistSets.Add(Line("t16-invoice", new ScriptedPostingEvent("SalesInvoicePosted", async (sp, c) =>
            {
                // Inventory: issues stock, then raises StockIssued carrying the cost it computed.
                await using var inventory = await ContextAsync(sp, c);
                inventory.PicklistSets.Add(Line("t16-issue-cost-12.5000", new ScriptedPostingEvent("StockIssued", async (sp2, c2) =>
                {
                    // Accounting: subscribes to StockIssued, never to SalesInvoicePosted.
                    await using var accounting = await ContextAsync(sp2, c2);
                    costRowsSeenByAccounting = await accounting.PicklistSets.CountAsync(p => p.Value!.StartsWith("t16-issue-cost"), c2);
                    accounting.PicklistSets.Add(Line("t16-journal"));
                    await accounting.SaveChangesAsync(c2);
                })));
                await inventory.SaveChangesAsync(c);
            })));
            await db.SaveChangesAsync(ct);
            return Result<int>.Success(1);
        });

        result.Succeeded.Should().BeTrue();
        ScriptedPostingEventHandler.Dispatched.Should().Equal("SalesInvoicePosted", "StockIssued");
        costRowsSeenByAccounting.Should().Be(1);
        (await LinesAsync("t16")).Should().Be(3);
    }

    [Test]
    public async Task T16_AFailureInTheChainedHandler_RollsBackEverything_IncludingTheCommandsOwnWrites()
    {
        using var scope = CreateScope();

        var act = async () => await RunAsync(scope.ServiceProvider, new TxCommand(), async ct =>
        {
            await using var db = await ContextAsync(scope.ServiceProvider, ct);
            db.Tenants.Add(Header("t16f"));
            db.PicklistSets.Add(Line("t16f-invoice", new ScriptedPostingEvent("SalesInvoicePosted", async (sp, c) =>
            {
                await using var inventory = await ContextAsync(sp, c);
                inventory.PicklistSets.Add(Line("t16f-issue", new ScriptedPostingEvent("StockIssued", (_, _) =>
                    throw new InvalidOperationException("accounting failed"))));
                await inventory.SaveChangesAsync(c);
            })));
            await db.SaveChangesAsync(ct);
            return Result<int>.Success(1);
        });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("accounting failed");
        (await TenantsAsync("t16f")).Should().Be(0);
        (await LinesAsync("t16f")).Should().Be(0);
    }

    // ---- 17 (A5) -------------------------------------------------------------------------------

    [Test]
    public async Task T17_ANestedTransactionalRequest_CommitsWithTheOuterOne()
    {
        using var scope = CreateScope();
        var sp = scope.ServiceProvider;

        var result = await RunAsync(sp, new TxCommand(), async ct =>
        {
            await using var db = await ContextAsync(sp, ct);
            db.Tenants.Add(Header("t17-outer"));
            await db.SaveChangesAsync(ct);

            var inner = await RunAsync(sp, new IdemCommand(Guid.NewGuid()), async ict =>
            {
                await using var innerDb = await ContextAsync(sp, ict);
                ReferenceEquals(innerDb, db).Should().BeTrue("the inner request joined the outer unit of work");
                innerDb.Tenants.Add(Header("t17-inner"));
                await innerDb.SaveChangesAsync(ict);
                return Result<int>.Success(2);
            });
            inner.Succeeded.Should().BeTrue();
            return Result<int>.Success(1);
        });

        result.Succeeded.Should().BeTrue();
        (await TenantsAsync("t17")).Should().Be(2);
        (await ProcessedRequestsAsync()).Should().Be(0, "a joined request claims no RequestId of its own");
    }

    [Test]
    public async Task T17_WhenTheNestedRequestFails_NothingIsCommitted_EvenIfTheOuterHandlerIgnoresIt()
    {
        using var scope = CreateScope();
        var sp = scope.ServiceProvider;

        var result = await RunAsync(sp, new TxCommand(), async ct =>
        {
            await using var db = await ContextAsync(sp, ct);
            db.Tenants.Add(Header("t17f-outer"));
            await db.SaveChangesAsync(ct);

            await RunAsync(sp, new TxCommand(), async ict =>
            {
                await using var innerDb = await ContextAsync(sp, ict);
                innerDb.Tenants.Add(Header("t17f-inner"));
                await innerDb.SaveChangesAsync(ict);
                return Result<int>.Failure("inner failed");
            });
            return Result<int>.Success(1); // ignores the inner result
        });

        result.Succeeded.Should().BeFalse();
        result.Errors.Should().Equal("inner failed");
        (await TenantsAsync("t17f")).Should().Be(0);
    }

    // ---- 18 (A6) -------------------------------------------------------------------------------

    [Test]
    public async Task T18_ARetriedAttempt_GetsAFreshContextAndEmptyBuffers_AndTheHandlerRunsOncePerAttempt()
    {
        using var scope = CreateScope();
        var contexts = new List<IApplicationDbContext>();
        var publisher = new RecordingPublisher();

        var result = await RunAsync(scope.ServiceProvider, new TxCommand(), async ct =>
        {
            await using var db = await ContextAsync(scope.ServiceProvider, ct);
            contexts.Add(db);
            var attempt = contexts.Count;
            db.PicklistSets.Add(Line($"t18-attempt{attempt}",
                new ScriptedPostingEvent($"posting-{attempt}"), new AfterCommitProbe($"after-{attempt}")));
            await db.SaveChangesAsync(ct); // both events are now in the unit of work's buffers

            if (attempt == 1) throw new PostgresException("could not serialize access", "ERROR", "ERROR", "40001");
            return Result<int>.Success(attempt);
        }, publisher);

        result.Succeeded.Should().BeTrue();
        result.Data.Should().Be(2);
        contexts.Count.Should().Be(2, "the handler ran once per attempt");
        ReferenceEquals(contexts[1], contexts[0]).Should().BeFalse("each attempt gets a fresh context");
        ScriptedPostingEventHandler.Dispatched.Should().Equal(["posting-2"], "attempt 1's buffered posting event died with it");
        publisher.Published.OfType<AfterCommitProbe>().Select(e => e.Name).Should().Equal("after-2");
        (await LinesAsync("t18")).Should().Be(1);
    }

    // ---- 19 (A6) -------------------------------------------------------------------------------

    [Test]
    public async Task T19_AnExecutionContextCapturedInsideTheUnitOfWork_SeesNoUnitOfWorkAfterItEnds()
    {
        using var scope = CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        ExecutionContext? captured = null;
        var activeInside = false;

        await RunAsync(scope.ServiceProvider, new TxCommand(), ct =>
        {
            activeInside = unitOfWork.IsActive;
            captured = ExecutionContext.Capture();
            return ValueTask.FromResult(Result<int>.Success(1));
        });

        activeInside.Should().BeTrue();
        captured.Should().NotBeNull();

        bool? activeLater = null;
        IApplicationDbContext? contextLater = null;
        ExecutionContext.Run(captured!, _ =>
        {
            activeLater = unitOfWork.IsActive;
            contextLater = scope.ServiceProvider.GetRequiredService<IApplicationDbContextFactory>()
                .CreateAsync().AsTask().GetAwaiter().GetResult();
        }, null);

        activeLater.Should().BeFalse();
        await using (contextLater!)
        {
            (await contextLater!.Tenants.CountAsync()).Should().BeGreaterThanOrEqualTo(0, "a fresh, usable context - not the disposed one");
        }
    }
}

/// <summary>An awaitable barrier for N participants.</summary>
internal sealed class CountdownBarrier(int participants)
{
    private int _remaining = participants;
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task SignalAndWaitAsync()
    {
        if (Interlocked.Decrement(ref _remaining) == 0) _released.TrySetResult();
        return _released.Task;
    }
}
