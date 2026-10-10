// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Domain.Common.Entities;

/// <summary>
/// An entity that raises domain events. <c>DispatchDomainEventsInterceptor</c> publishes and clears
/// the events of every tracked entity implementing this, whatever its base class or key type.
/// </summary>
/// <remarks>
/// <see cref="BaseEntity{TKey}"/> implements it, so deriving from any of the template's bases is
/// enough. Implement it directly only for an entity that cannot derive from one. Its events are not
/// persisted: keep <see cref="DomainEvents"/> unmapped (<c>[NotMapped]</c> or <c>Ignore</c>).
/// </remarks>
public interface IHasDomainEvents
{
    IReadOnlyCollection<DomainEvent> DomainEvents { get; }

    void AddDomainEvent(DomainEvent domainEvent);

    void ClearDomainEvents();
}
