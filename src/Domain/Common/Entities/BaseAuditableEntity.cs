// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Domain.Common.Entities;

/// <summary>
/// An entity whose creation and last modification are stamped by <c>AuditableEntityInterceptor</c>,
/// for any key type (Pass 55; see <see cref="BaseEntity{TKey}"/>).
/// </summary>
/// <remarks>
/// Stamping keys off <see cref="IAuditableEntity"/>, not this class, so an entity that cannot derive
/// from it can implement the interface and be stamped the same way. Note that
/// <see cref="BaseAuditableEntity"/> is now <c>BaseAuditableEntity&lt;int&gt;</c> - a
/// <c>BaseEntity&lt;int&gt;</c>, no longer a <see cref="BaseEntity"/>. Code that needs "any entity"
/// should test for <see cref="IHasDomainEvents"/>, <see cref="IAuditableEntity"/> or
/// <see cref="IEntity"/>, never for a base class.
/// </remarks>
public abstract class BaseAuditableEntity<TKey> : BaseEntity<TKey>, IAuditableEntity
    where TKey : IEquatable<TKey>
{
    public virtual DateTime? CreatedAt { get; set; }

    public virtual string? CreatedById { get; set; }

    public virtual DateTime? LastModifiedAt { get; set; }

    public virtual string? LastModifiedById { get; set; }
}

/// <summary>The <c>int</c>-keyed auditable base, unchanged for every existing entity.</summary>
public abstract class BaseAuditableEntity : BaseAuditableEntity<int>
{
}

public interface IAuditableEntity
{
    DateTime? CreatedAt { get; set; }

    string? CreatedById { get; set; }

   DateTime? LastModifiedAt { get; set; }

    string? LastModifiedById { get; set; }
}
