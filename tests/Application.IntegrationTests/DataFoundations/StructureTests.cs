using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Branches;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Transactions;
using CleanArchitecture.Blazor.Application.Common.Transactions;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Rules;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using static CleanArchitecture.Blazor.Application.IntegrationTests.DataFoundations.ModelHarness;

namespace CleanArchitecture.Blazor.Application.IntegrationTests.DataFoundations;

/// <summary>
/// Pass 60 plan §5 rows 43 (amendment C3: a module entity with no DbSet) and 45 and 46 (amendment C4: the
/// sanctioned insert-if-absent).
/// </summary>
[NonParallelizable]
public class StructureTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    [OneTimeSetUp]
    public async Task Ready() => await RootAsync();

    private static string Tag() => "s" + Guid.NewGuid().ToString("N")[..10];

    // ---- 43: C3 ----------------------------------------------------------------------------------------

    [Test]
    public async Task T43_AModuleEntityWithNoDbSet_IsMappedFilteredGuardedAndWritable()
    {
        var b1 = await CreateBranchAsync();
        var b2 = await CreateBranchAsync();
        var (scope, db) = await ContextAsync();
        using (scope)
        await using (db)
        {
            // Mapped, with the conventions applied, although no DbSet property names it.
            typeof(TestModelDbContext).GetProperties().Should().NotContain(p => p.PropertyType == typeof(DbSet<AlphaThing>));
            var entity = db.Model.FindEntityType(typeof(AlphaThing))!;
            entity.Should().NotBeNull();
            entity.GetSchema().Should().Be("core"); // Pass 60c: every module's tables are in core
            (entity.FindProperty(nameof(AlphaThing.OnHand))!.GetPrecision(), entity.FindProperty(nameof(AlphaThing.OnHand))!.GetScale())
                .Should().Be((18, 4));
            entity.FindDeclaredQueryFilter(QueryFilters.Branch).Should().NotBeNull("the branch filter loop iterates the model");
            DataModelGuard.FindProblems(db.Model).Should().BeEmpty();
        }

        // Written through Set<T>() under each branch's grant, as BranchAccessBehaviour installs it.
        var user = await CreateUserAsync([b1]);
        var name1 = Tag();
        var name2 = Tag();
        using (Accessor.Push(ModelHarness.System))
        {
            var (s, d) = await ContextAsync();
            using (s)
            await using (d)
            {
                d.Set<AlphaThing>().AddRange(new AlphaThing { BranchId = b1, Name = name1 }, new AlphaThing { BranchId = b2, Name = name2 });
                await d.SaveChangesAsync();
            }
        }

        // Read through the branch filter: a user of branch 1 sees branch 1's row only.
        using (Accessor.Push(Reader(user, Reads(b1))))
        {
            var (s, d) = await ContextAsync();
            using (s)
            await using (d)
            {
                var seen = await d.Set<AlphaThing>().Where(t => t.Name == name1 || t.Name == name2).Select(t => t.Name).ToListAsync();
                seen.Should().Equal(name1);

                // Refused at save outside the branch grant, as any branch-scoped write is.
                d.Set<AlphaThing>().Add(new AlphaThing { BranchId = b1, Name = Tag() });
                await FluentActions.Invoking(() => d.SaveChangesAsync()).Should().ThrowAsync<DataRuleViolationException>()
                    .WithMessage("*AlphaThing*outside a branch command*");
            }
        }

        // And within the grant, the same write succeeds.
        using (Accessor.Push(Reader(user, Reads(b1))))
        using (BranchGrant.Begin(b1))
        {
            var (s, d) = await ContextAsync();
            using (s)
            await using (d)
            {
                d.Set<AlphaThing>().Add(new AlphaThing { BranchId = b1, Name = Tag(), OnHand = 1.5m });
                (await d.SaveChangesAsync()).Should().Be(1);

                d.Set<AlphaThing>().Add(new AlphaThing { BranchId = b1, Name = Tag(), OnHand = 1.00001m });
                await FluentActions.Invoking(() => d.SaveChangesAsync()).Should().ThrowAsync<DataRuleViolationException>()
                    .WithMessage("*AlphaThing*.OnHand is 1.00001*");
            }
        }
    }

    // ---- 45: C4 under concurrency ----------------------------------------------------------------------

    [Test]
    public async Task T45_TwoTransactionsEnsureTheSameMissingRow_BothProceed_AndExactlyOneRowExists()
    {
        var branch = await CreateBranchAsync();
        var code = Tag();
        var aEnsured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bStarting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = await RootAsync();

        async Task<bool> WorkerAsync(bool first)
        {
            using var _ = Accessor.Push(ModelHarness.System);
            using var scope = services.CreateScope();
            var manager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            await using var session = await manager.BeginAsync(CancellationToken.None);
            var ensure = scope.ServiceProvider.GetRequiredService<IInsertIfAbsent>();

            if (!first)
            {
                await aEnsured.Task;
                bStarting.SetResult();
            }

            // B's ensure blocks on A's uncommitted row until A commits, then does nothing.
            var inserted = await ensure.EnsureAsync(new TestBalance { ItemCode = code, BranchId = branch }, b => new { b.ItemCode, b.BranchId });

            if (first)
            {
                aEnsured.SetResult();
                await bStarting.Task;
                await Task.Delay(300); // let B reach the conflicting insert and wait on it
            }

            var context = session.Context;
            var row = await context.Set<TestBalance>().Where(b => b.ItemCode == code && b.BranchId == branch).ForUpdate().SingleAsync();
            row.OnHand += 1;
            await context.SaveChangesAsync(CancellationToken.None);
            await session.CommitAsync(CancellationToken.None);
            return inserted;
        }

        var a = WorkerAsync(first: true);
        var b = WorkerAsync(first: false);
        var results = await Task.WhenAll(a, b).WaitAsync(Guard);

        results.Should().Equal(true, false);
        using (Accessor.Push(ModelHarness.System))
        {
            var (s, d) = await ContextAsync();
            using (s)
            await using (d)
            {
                var rows = await d.Set<TestBalance>().Where(r => r.ItemCode == code).ToListAsync();
                rows.Should().ContainSingle().Which.OnHand.Should().Be(2m, "both transactions locked the one row in turn");
            }
        }
    }

    // ---- 46: C4's refusals and stamps ------------------------------------------------------------------

    private static async Task<T> InUnitOfWorkAsync<T>(UserContext user, Func<IInsertIfAbsent, Task<T>> body)
    {
        var services = await RootAsync();
        using var _ = Accessor.Push(user);
        using var scope = services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        await using var session = await manager.BeginAsync(CancellationToken.None);
        var result = await body(scope.ServiceProvider.GetRequiredService<IInsertIfAbsent>());
        await session.CommitAsync(CancellationToken.None);
        return result;
    }

    [Test]
    public async Task T46_OutsideAUnitOfWork_IsRefused()
    {
        var services = await RootAsync();
        using var scope = services.CreateScope();
        var ensure = scope.ServiceProvider.GetRequiredService<IInsertIfAbsent>();
        await FluentActions.Invoking(() => ensure.EnsureAsync(new TestBalance { ItemCode = Tag(), BranchId = 1 }, b => new { b.ItemCode, b.BranchId }))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*inside the unit of work only*");
    }

    [Test]
    public async Task T46_AKeyThatIsNotUnique_AndTheTypesItCannotHonour_AreRefused()
    {
        var branch = await CreateBranchAsync();
        await InUnitOfWorkAsync(ModelHarness.System, async ensure =>
        {
            await FluentActions.Invoking(() => ensure.EnsureAsync(new TestBalance { ItemCode = Tag(), BranchId = branch }, b => b.ItemCode))
                .Should().ThrowAsync<InvalidOperationException>().WithMessage("*(ItemCode) is not its primary key*");
            await FluentActions.Invoking(() => ensure.EnsureAsync(new Branch { Code = Tag(), Name = "x" }, b => b.Code))
                .Should().ThrowAsync<InvalidOperationException>().WithMessage("*Branch*IAuditable*");
            await FluentActions.Invoking(() => ensure.EnsureAsync(new TestPostedDocument(), d => d.Id))
                .Should().ThrowAsync<InvalidOperationException>().WithMessage("*TestPostedDocument*never created by key*");
            await FluentActions.Invoking(() => ensure.EnsureAsync(new TestTenantRow { Code = Tag(), TenantId = "t" }, t => t.Code))
                .Should().ThrowAsync<InvalidOperationException>().WithMessage("*TestTenantRow*tenant-scoped*");
            await FluentActions.Invoking(() => ensure.EnsureAsync(new TestBalance { ItemCode = Tag(), BranchId = branch, OnHand = 1.00001m },
                    b => new { b.ItemCode, b.BranchId }))
                .Should().ThrowAsync<InvalidOperationException>().WithMessage("*TestBalance.OnHand*decimal places*");
            return 0;
        });
    }

    [Test]
    public async Task T46_ABranchScopedRow_NeedsTheBranchGrant()
    {
        var b1 = await CreateBranchAsync();
        var b2 = await CreateBranchAsync();
        var user = Reader(await CreateUserAsync([b1]), Reads(b1));

        await InUnitOfWorkAsync(user, async ensure =>
        {
            await FluentActions.Invoking(() => ensure.EnsureAsync(new TestBalance { ItemCode = Tag(), BranchId = b1 }, b => new { b.ItemCode, b.BranchId }))
                .Should().ThrowAsync<InvalidOperationException>().WithMessage("*outside a branch command*");

            using (BranchGrant.Begin(b1))
            {
                await FluentActions.Invoking(() => ensure.EnsureAsync(new TestBalance { ItemCode = Tag(), BranchId = b2 }, b => new { b.ItemCode, b.BranchId }))
                    .Should().ThrowAsync<InvalidOperationException>().WithMessage($"*belongs to branch {b2}*");
                (await ensure.EnsureAsync(new TestBalance { ItemCode = Tag(), BranchId = b1 }, b => new { b.ItemCode, b.BranchId }))
                    .Should().BeTrue();
            }
            return 0;
        });
    }

    [Test]
    public async Task T46_TheCreationAuditFieldsAreStamped_AndASecondEnsureDoesNothing()
    {
        var code = Tag();
        var userId = await CreateUserAsync([]);
        var first = await InUnitOfWorkAsync(Reader(userId, Reads()), e => e.EnsureAsync(new TestStampedRow { Code = code }, r => r.Code));
        var second = await InUnitOfWorkAsync(Reader(userId, Reads()), e => e.EnsureAsync(new TestStampedRow { Code = code }, r => r.Code));

        first.Should().BeTrue();
        second.Should().BeFalse();
        using (Accessor.Push(ModelHarness.System))
        {
            var (s, d) = await ContextAsync();
            using (s)
            await using (d)
            {
                var row = await d.Set<TestStampedRow>().SingleAsync(r => r.Code == code);
                row.CreatedById.Should().Be(userId);
                row.CreatedAt.Should().NotBeNull().And.BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(5));
            }
        }
    }
}
