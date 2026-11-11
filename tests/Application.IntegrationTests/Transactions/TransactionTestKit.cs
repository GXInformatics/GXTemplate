#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Transactions;
using CleanArchitecture.Blazor.Application.Common.Models;
using CleanArchitecture.Blazor.Application.Pipeline;
using CleanArchitecture.Blazor.Domain.Common;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CleanArchitecture.Blazor.Application.IntegrationTests.Transactions;

using static Testing;

/// <summary>
/// A posting event whose handler runs <see cref="Body"/> - so each test states, next to its command,
/// what "Inventory" or "Accounting" does inside the transaction (Pass 58).
/// </summary>
public sealed class ScriptedPostingEvent : PostingEvent
{
    public ScriptedPostingEvent(string name, Func<IServiceProvider, CancellationToken, Task>? body = null)
    {
        Name = name;
        Body = body ?? ((_, _) => Task.CompletedTask);
    }

    public string Name { get; }
    public Func<IServiceProvider, CancellationToken, Task> Body { get; }
}

/// <summary>Registered by the harness through <c>AddPostingEventHandlers</c>, as a module's would be.</summary>
public sealed class ScriptedPostingEventHandler(IServiceProvider services) : IPostingEventHandler<ScriptedPostingEvent>
{
    /// <summary>Names of the events dispatched, in order. Reset by <see cref="TransactionKit.Reset"/>.</summary>
    public static readonly ConcurrentQueue<string> Dispatched = new();

    public async ValueTask Handle(ScriptedPostingEvent postingEvent, CancellationToken cancellationToken)
    {
        Dispatched.Enqueue(postingEvent.Name);
        await postingEvent.Body(services, cancellationToken);
    }
}

/// <summary>An ordinary event, published after the commit.</summary>
public sealed class AfterCommitProbe(string name) : DomainEvent
{
    public string Name { get; } = name;
}

public sealed class TxCommand : IRequest<Result<int>>, ITransactionalRequest;

public sealed class IdemCommand(Guid requestId) : IRequest<Result<int>>, IIdempotentRequest
{
    public Guid RequestId { get; } = requestId;
}

public sealed class OtherIdemCommand(Guid requestId) : IRequest<Result<int>>, IIdempotentRequest
{
    public Guid RequestId { get; } = requestId;
}

/// <summary>Records what is published, and what the world looked like at that moment.</summary>
public sealed class RecordingPublisher(Func<INotification, Task>? onPublish = null) : IPublisher
{
    public List<INotification> Published { get; } = new();

    public async ValueTask Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification
    {
        Published.Add(notification);
        if (onPublish is not null) await onPublish(notification);
    }

    public ValueTask Publish(object notification, CancellationToken cancellationToken = default) =>
        Publish((INotification)notification, cancellationToken);
}

/// <summary>Builds the real behaviour from a scope and runs a handler delegate through it.</summary>
public static class TransactionKit
{
    public static void Reset()
    {
        ScriptedPostingEventHandler.Dispatched.Clear();
    }

    /// <summary>
    /// The real <see cref="TransactionBehaviour{TRequest,TResponse}"/>, resolved from
    /// <paramref name="services"/>; <paramref name="overrides"/> replace constructor dependencies
    /// (a <see cref="RecordingPublisher"/>, typically).
    /// </summary>
    public static ValueTask<Result<int>> RunAsync<TRequest>(
        IServiceProvider services,
        TRequest request,
        Func<CancellationToken, ValueTask<Result<int>>> handler,
        params object[] overrides)
        where TRequest : class, IMessage, ITransactionalRequest
    {
        var behaviour = ActivatorUtilities.CreateInstance<TransactionBehaviour<TRequest, Result<int>>>(services, overrides);
        return behaviour.Handle(request, (_, ct) => handler(ct), CancellationToken.None);
    }

    public static async Task<IApplicationDbContext> ContextAsync(IServiceProvider services, CancellationToken ct = default) =>
        await services.GetRequiredService<IApplicationDbContextFactory>().CreateAsync(ct);

    /// <summary>A tenant row standing in for a document header: Guid v7 key, Description as status.</summary>
    public static Tenant Header(string prefix, string status = "Draft") =>
        new() { Id = prefix + "-" + Guid.CreateVersion7(), Name = prefix, Description = status };

    public static PicklistSet Line(string value, params DomainEvent[] events)
    {
        var line = new PicklistSet { Name = Picklist.Unit, Value = value, Text = value };
        foreach (var domainEvent in events) line.AddDomainEvent(domainEvent);
        return line;
    }

    public static async Task<int> TenantsAsync(string prefix)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Tenants.CountAsync(t => t.Id.StartsWith(prefix));
    }

    public static async Task<int> LinesAsync(string prefix)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.PicklistSets.IgnoreQueryFilters().CountAsync(p => p.Value!.StartsWith(prefix));
    }

    public static async Task<int> AuditRowsAsync()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.AuditTrails.IgnoreQueryFilters([QueryFilters.Tenant]).CountAsync();
    }

    public static async Task<int> ProcessedRequestsAsync()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.ProcessedRequests.CountAsync();
    }
}
