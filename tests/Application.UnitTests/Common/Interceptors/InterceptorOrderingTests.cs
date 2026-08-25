#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Infrastructure;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Domain.Common;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Domain.Events;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Mediator;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Common.Interceptors;

/// <summary>
/// The order in which the two save-changes interceptors are registered is load-bearing, and nothing
/// about the code makes that visible at a glance - hence this test.
/// <para>
/// <see cref="AuditableEntityInterceptor"/> opens a transaction in SavingChanges and holds it across
/// the save. EF invokes interceptors in registration order, so registering it first means its commit
/// runs before <see cref="DispatchDomainEventsInterceptor"/> publishes - domain events are never
/// published for a change whose audit write subsequently rolled it back.
/// </para>
/// </summary>
[TestFixture]
public class InterceptorOrderingTests
{
    [Test]
    public void AuditableEntityInterceptorIsRegisteredBeforeDispatchDomainEventsInterceptor()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DatabaseSettings:DBProvider"] = "sqlite",
                ["DatabaseSettings:ConnectionString"] = "Data Source=:memory:"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(configuration);

        var registered = services
            .Where(d => d.ServiceType == typeof(ISaveChangesInterceptor))
            .Select(d => d.ImplementationType)
            .ToList();

        registered.Should().HaveCount(2);
        registered[0].Should().Be(typeof(AuditableEntityInterceptor),
            "the audit interceptor holds the transaction and must commit before events are published");
        registered[1].Should().Be(typeof(DispatchDomainEventsInterceptor));
    }

    [Test]
    public async Task BothInterceptorsTogetherCompleteASaveWithoutTransactionConflict()
    {
        // The real regression this pins: the dispatch interceptor used to open a transaction of its
        // own, which throws once the audit interceptor is holding one ("The connection is already in
        // a transaction and cannot participate in another"). Deleting an entity that carries a domain
        // event is what drives the dispatch interceptor's SavingChanges branch.
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var userContext = new Mock<IUserContextAccessor>();
        userContext.SetupGet(x => x.Current).Returns(new UserContext("ordering-user", "orderer"));
        var dateTime = new Mock<IDateTime>();
        dateTime.SetupGet(x => x.Now).Returns(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        var mediator = new Mock<IMediator>();
        mediator.Setup(x => x.Publish(It.IsAny<DomainEvent>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(
                new AuditableEntityInterceptor(userContext.Object, dateTime.Object),
                new DispatchDomainEventsInterceptor(mediator.Object))
            .Options;

        await using var context = new ApplicationDbContext(options);
        await context.Database.EnsureCreatedAsync();
        context.Users.Add(new ApplicationUser
        {
            Id = "ordering-user", UserName = "orderer", Email = "orderer@example.com"
        });
        await context.SaveChangesAsync();

        var item = new PicklistSet
        {
            Name = Picklist.Brand, Value = "ordering", Text = "Ordering", Description = "d"
        };
        context.PicklistSets.Add(item);
        await context.SaveChangesAsync();

        item.AddDomainEvent(new PicklistSetDeletedEvent(item));
        context.PicklistSets.Remove(item);

        var act = async () => await context.SaveChangesAsync();

        await act.Should().NotThrowAsync("the two interceptors must not fight over the transaction");
        (await context.AuditTrails.CountAsync()).Should().BeGreaterThan(0);
        mediator.Verify(x => x.Publish(It.IsAny<DomainEvent>(), It.IsAny<CancellationToken>()), Times.Once);

        await connection.DisposeAsync();
    }
}
#nullable restore
