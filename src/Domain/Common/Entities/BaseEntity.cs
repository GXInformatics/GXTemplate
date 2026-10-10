// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel.DataAnnotations.Schema;

namespace CleanArchitecture.Blazor.Domain.Common.Entities;

/// <summary>
/// The base every project entity derives from, for any key type. Implementing
/// <see cref="IBusinessEntity"/> here rather than on each entity is what makes the GX table-naming
/// convention automatic - see <see cref="IBusinessEntity"/> for the schema and prefix rules, and for
/// why the template's own entities stay outside them.
/// </summary>
/// <remarks>
/// <b>Generic since Pass 55.</b> It used to fix the key at <c>int</c>, so an entity keyed by
/// <c>long</c>, <see cref="Guid"/> or a natural string key could not derive from it - and with it lost
/// domain-event dispatch (which iterated the concrete <see cref="BaseEntity"/>) and, through
/// <see cref="BaseAuditableEntity"/>, audit stamping. <see cref="BaseEntity"/> is now
/// <c>BaseEntity&lt;int&gt;</c>, so every existing entity is unchanged, and dispatch keys off
/// <see cref="IHasDomainEvents"/> rather than any base class.
/// </remarks>
/// <typeparam name="TKey">The primary key type: <c>int</c>, <c>long</c>, <see cref="Guid"/>, <c>string</c>.</typeparam>
public abstract class BaseEntity<TKey> : IEntity<TKey>, IBusinessEntity, IHasDomainEvents
    where TKey : IEquatable<TKey>
{
    private readonly List<DomainEvent> _domainEvents = new();

    [NotMapped] public IReadOnlyCollection<DomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    public virtual TKey Id { get; set; } = default!;

    public void AddDomainEvent(DomainEvent domainEvent)
    {
        _domainEvents.Add(domainEvent);
    }

    public void RemoveDomainEvent(DomainEvent domainEvent)
    {
        _domainEvents.Remove(domainEvent);
    }

    public void ClearDomainEvents()
    {
        _domainEvents.Clear();
    }
}

/// <summary>
/// The <c>int</c>-keyed base - what every entity derived from before Pass 55, and still the default.
/// </summary>
public abstract class BaseEntity : BaseEntity<int>
{
}
