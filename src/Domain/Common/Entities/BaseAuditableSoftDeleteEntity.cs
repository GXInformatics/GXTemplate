// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Domain.Common.Entities;

public abstract class BaseAuditableSoftDeleteEntity<TKey> : BaseAuditableEntity<TKey>, ISoftDelete
    where TKey : IEquatable<TKey>
{
    public DateTime? DeletedAt { get; set; }
    public string? DeletedById { get; set; }
}

/// <summary>The <c>int</c>-keyed soft-delete base (Pass 55 made the key generic).</summary>
public abstract class BaseAuditableSoftDeleteEntity : BaseAuditableSoftDeleteEntity<int>
{
}
