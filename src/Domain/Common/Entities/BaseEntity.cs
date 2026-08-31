// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel.DataAnnotations.Schema;

namespace CleanArchitecture.Blazor.Domain.Common.Entities;

/// <summary>
/// The base every project entity derives from. Implementing <see cref="IBusinessEntity"/> here
/// rather than on each entity is what makes the GX table-naming convention automatic - see
/// <see cref="IBusinessEntity"/> for the schema and prefix rules, and for why the template's own
/// entities stay outside them.
/// </summary>
public abstract class BaseEntity : IEntity<int>, IBusinessEntity
{
    private readonly List<DomainEvent> _domainEvents = new();

    [NotMapped] public IReadOnlyCollection<DomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    public virtual int Id { get; set; }

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
