using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;
using CleanArchitecture.Blazor.Application.Pipeline;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Rules;
using FluentAssertions;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using static CleanArchitecture.Blazor.Application.IntegrationTests.DataFoundations.ModelHarness;

namespace CleanArchitecture.Blazor.Application.IntegrationTests.DataFoundations;

/// <summary>A cacheable probe; its handler is the delegate each test passes to the behaviour.</summary>
public sealed class CachedProbe(string key) : ICacheableRequest<int>
{
    public string CacheKey { get; } = key;
    public CacheScope Scope => CacheScope.Global;
    public IEnumerable<string>? Tags => ["p60-cache-probe"];
}

/// <summary>
/// Architecture rule 3 (Pass 60 plan §5 row 21): a cacheable request does not read an append-only or branch-scoped
/// table. Checked at run time, by the real FusionCacheBehaviour and the real command interceptor.
/// </summary>
[NonParallelizable]
public class CacheRuleTests
{
    [OneTimeSetUp]
    public async Task Ready() => await RootAsync();

    private static async Task<int> ThroughTheCacheAsync(Func<TestModelDbContext, Task<int>> query)
    {
        using var _ = Accessor.Push(ModelHarness.System);
        var (scope, db) = await ContextAsync();
        using (scope)
        await using (db)
        {
            var behaviour = ActivatorUtilities.CreateInstance<FusionCacheBehaviour<CachedProbe, int>>(scope.ServiceProvider);
            return await behaviour.Handle(new CachedProbe("p60-" + Guid.NewGuid().ToString("N")),
                async (_, _) => await query(db), CancellationToken.None);
        }
    }

    [Test]
    public async Task T21_ACacheableQueryOfALedger_IsRefused_NamingTheRequestAndTheTable()
    {
        await FluentActions.Invoking(() => ThroughTheCacheAsync(db => db.TestLedgerRows.CountAsync()))
            .Should().ThrowAsync<DataRuleViolationException>()
            .WithMessage("*CachedProbe is cacheable but reads TBL_TEST_LEDGER_ROW*");
    }

    [Test]
    public async Task T21_ACacheableQueryOfABranchScopedTable_IsRefused()
    {
        await FluentActions.Invoking(() => ThroughTheCacheAsync(db => db.TestBranchRows.CountAsync()))
            .Should().ThrowAsync<DataRuleViolationException>()
            .WithMessage("*CachedProbe*TBL_TEST_BRANCH_ROW*");

        // A join brings the table in just the same.
        await FluentActions.Invoking(() => ThroughTheCacheAsync(db =>
                db.PicklistSets.Where(p => db.TestBranchRows.Any(r => r.Name == p.Value)).CountAsync()))
            .Should().ThrowAsync<DataRuleViolationException>().WithMessage("*TBL_TEST_BRANCH_ROW*");
    }

    [Test]
    public async Task T21_ACacheableQueryOfOrdinaryData_IsUnaffected()
    {
        var count = await ThroughTheCacheAsync(db => db.PicklistSets.CountAsync());
        count.Should().BeGreaterThanOrEqualTo(0);
    }

    [Test]
    public async Task T21_TheSameLedgerQuery_WithoutTheCache_IsUnaffected()
    {
        using var _ = Accessor.Push(ModelHarness.System);
        var (scope, db) = await ContextAsync();
        using (scope)
        await using (db)
        {
            CacheFill.Current.Should().BeNull();
            (await db.TestLedgerRows.CountAsync()).Should().BeGreaterThanOrEqualTo(0);
        }
    }
}
