#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;

using CleanArchitecture.Blazor.Domain.Common;
using CleanArchitecture.Blazor.Domain.Common.Entities;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Domain.Events;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Interceptors;
using Mediator;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Common.Interceptors;

[TestFixture]
public class SaveChangesInterceptorRegressionTests
{
    [Test]
    public async Task DispatchDomainEventsInterceptor_ShouldPublishAndClearDomainEvents()
    {
        var mediator = new Mock<IMediator>();
        mediator.Setup(x => x.Publish(It.IsAny<DomainEvent>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        await using var context = await CreateContextAsync(new DispatchDomainEventsInterceptor(mediator.Object));

        var picklistSet = new PicklistSet { Value = "Regression Test Value" };
        picklistSet.AddDomainEvent(new PicklistSetCreatedEvent(picklistSet));

        context.PicklistSets.Add(picklistSet);
        await context.SaveChangesAsync();

        mediator.Verify(x => x.Publish(
                It.Is<DomainEvent>(evt => evt.GetType() == typeof(PicklistSetCreatedEvent) &&
                                          ((PicklistSetCreatedEvent)evt).Item == picklistSet),
                It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.That(picklistSet.DomainEvents, Is.Empty);
    }

    [Test]
    public async Task DispatchDomainEventsInterceptor_ShouldPublishUpdatedPicklistSetDomainEvents()
    {
        var mediator = new Mock<IMediator>();
        mediator.Setup(x => x.Publish(It.IsAny<DomainEvent>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        await using var context = await CreateContextAsync(new DispatchDomainEventsInterceptor(mediator.Object));

        var picklistSet = new PicklistSet { Value = "Updated Value" };
        context.PicklistSets.Add(picklistSet);
        await context.SaveChangesAsync();

        mediator.Reset();
        mediator.Setup(x => x.Publish(It.IsAny<DomainEvent>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        picklistSet.Description = "Updated description";
        picklistSet.AddDomainEvent(new PicklistSetUpdatedEvent(picklistSet));

        await context.SaveChangesAsync();

        mediator.Verify(x => x.Publish(
                It.Is<DomainEvent>(evt => evt.GetType() == typeof(PicklistSetUpdatedEvent) &&
                                          ((PicklistSetUpdatedEvent)evt).Item == picklistSet),
                It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.That(picklistSet.DomainEvents, Is.Empty);
    }

    [Test]
    public async Task DispatchDomainEventsInterceptor_ShouldPublishDeletedPicklistSetDomainEvents()
    {
        var mediator = new Mock<IMediator>();
        mediator.Setup(x => x.Publish(It.IsAny<DomainEvent>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        await using var context = await CreateContextAsync(new DispatchDomainEventsInterceptor(mediator.Object));

        var picklistSet = new PicklistSet { Value = "Deleted Value" };
        context.PicklistSets.Add(picklistSet);
        await context.SaveChangesAsync();

        mediator.Reset();
        mediator.Setup(x => x.Publish(It.IsAny<DomainEvent>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        picklistSet.AddDomainEvent(new PicklistSetDeletedEvent(picklistSet));
        context.PicklistSets.Remove(picklistSet);

        await context.SaveChangesAsync();

        mediator.Verify(x => x.Publish(
                It.Is<DomainEvent>(evt => evt.GetType() == typeof(PicklistSetDeletedEvent) &&
                                          ((PicklistSetDeletedEvent)evt).Item == picklistSet),
                It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.That(picklistSet.DomainEvents, Is.Empty);
    }

    [Test]
    public async Task AuditableEntityInterceptor_ShouldWriteAuditTrailsInTheSameSaveAsTheEntity()
    {
        // This assertion is the inverse of the one it replaces. Audit rows used to be handed to a
        // notification handler that persisted them on another context, so the test verified that an
        // AuditTrailsReadyEvent was published. They are now written in the same transaction, so what
        // matters is that the rows are in the database when SaveChangesAsync returns.
        var userContextAccessor = new Mock<IUserContextAccessor>();
        userContextAccessor.SetupGet(x => x.Current)
            .Returns(new UserContext("user-123", "regression-user"));

        var dateTime = new Mock<IDateTime>();
        dateTime.SetupGet(x => x.UtcNow).Returns(new DateTime(2026, 3, 29, 9, 0, 0, DateTimeKind.Utc));

        await using var context = await CreateContextAsync(
            new AuditableEntityInterceptor(userContextAccessor.Object, dateTime.Object));

        // AuditTrail.UserId is a real foreign key to AspNetUsers, so the acting user must exist.
        // Under the previous design a dangling id only lost the audit row (the handler swallowed the
        // failure); it now rolls the whole operation back, which is the point of the redesign.
        context.Users.Add(new CleanArchitecture.Blazor.Domain.Identity.ApplicationUser
        {
            Id = "user-123", UserName = "regression-user", Email = "regression-user@example.com"
        });
        await context.SaveChangesAsync();

        context.PicklistSets.Add(new PicklistSet
        {
            Name = Picklist.Brand,
            Value = "regression-value",
            Text = "Regression Value",
            Description = "audit regression"
        });

        await context.SaveChangesAsync();

        var auditTrails = await context.AuditTrails.ToListAsync();
        Assert.That(auditTrails, Has.Count.EqualTo(1));
        Assert.That(auditTrails[0].TableName, Is.EqualTo(nameof(PicklistSet)));
        Assert.That(auditTrails[0].UserId, Is.EqualTo("user-123"));
        Assert.That(auditTrails[0].AuditType, Is.EqualTo(AuditType.Create));
    }

    private static async Task<ApplicationDbContext> CreateContextAsync(params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptors)
            .Options;

        var context = new ApplicationDbContext(options);
        await context.Database.EnsureCreatedAsync();
        return context;
    }
}
