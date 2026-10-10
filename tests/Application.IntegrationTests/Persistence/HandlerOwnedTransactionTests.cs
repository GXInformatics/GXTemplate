using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.IntegrationTests.Persistence;

using static Testing;

/// <summary>
/// Pass 55: an Application-layer handler owns a transaction through
/// <see cref="IApplicationDbContext.Database"/>, without casting to the concrete context.
/// </summary>
public class HandlerOwnedTransactionTests : TestBase
{
    /// <summary>
    /// Shaped exactly like a Mediator handler: it depends on the factory INTERFACE and nothing from
    /// Infrastructure. Two saves that must stand or fall together - the header-and-lines case.
    /// </summary>
    private sealed class TwoSavesInOneTransaction(IApplicationDbContextFactory dbContextFactory)
    {
        public bool SawItsOwnTransaction { get; private set; }

        public async Task HandleAsync(string prefix, bool failBeforeCommit, CancellationToken cancellationToken)
        {
            await using var db = await dbContextFactory.CreateAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            SawItsOwnTransaction = db.Database.CurrentTransaction is not null;

            db.Tenants.Add(new Tenant { Id = prefix + "-header", Name = prefix + " header" });
            await db.SaveChangesAsync(cancellationToken);

            db.Tenants.Add(new Tenant { Id = prefix + "-line", Name = prefix + " line" });
            await db.SaveChangesAsync(cancellationToken);

            if (failBeforeCommit)
                throw new InvalidOperationException("the second half of the work failed");

            await transaction.CommitAsync(cancellationToken);
        }
    }

    private static TwoSavesInOneTransaction Handler() =>
        new(CreateScope().ServiceProvider.GetRequiredService<IApplicationDbContextFactory>());

    private static async Task<(int Tenants, int AuditRows)> CountAsync(string prefix)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var tenants = await db.Tenants.CountAsync(t => t.Id.StartsWith(prefix));
        var audit = await db.AuditTrails.IgnoreQueryFilters([QueryFilters.Tenant])
            .CountAsync(a => a.TableName == nameof(Tenant));
        return (tenants, audit);
    }

    [Test]
    public void TheInterfaceExposesTheDatabaseFacade()
    {
        typeof(IApplicationDbContext).GetProperty(nameof(IApplicationDbContext.Database))!.PropertyType
            .Should().Be(typeof(Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade));
    }

    [Test]
    public async Task AHandler_BeginsAndCommits_ThroughTheInterface()
    {
        var handler = Handler();

        await handler.HandleAsync("commit", failBeforeCommit: false, CancellationToken.None);

        handler.SawItsOwnTransaction.Should().BeTrue();
        var (tenants, audit) = await CountAsync("commit");
        tenants.Should().Be(2);
        audit.Should().Be(2, "the audit interceptor joins the handler's transaction rather than opening its own");
    }

    [Test]
    public async Task WithoutTheCommit_BothSaves_AndTheirAuditRows_RollBack()
    {
        // The control that makes the commit test mean something: two SaveChanges calls that each
        // succeeded are both undone, because they ran inside the handler's transaction.
        var handler = Handler();

        var act = () => handler.HandleAsync("rollback", failBeforeCommit: true, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        var (tenants, audit) = await CountAsync("rollback");
        tenants.Should().Be(0);
        audit.Should().Be(0);
    }
}
