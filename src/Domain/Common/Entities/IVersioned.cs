namespace CleanArchitecture.Blazor.Domain.Common.Entities;

/// <summary>
/// Opt-in optimistic concurrency (Pass 59). <see cref="Version"/> is PostgreSQL's <c>xmin</c> system
/// column, mapped as a row version: every update of the row changes it.
/// </summary>
/// <remarks>
/// <b>Use on master data and document headers.</b> Ledger rows do not need it: they are
/// <see cref="IAppendOnly"/> and never updated.
/// <para>
/// <b>An edit is only protected if the version the user loaded travels with it.</b> Each handler opens a
/// fresh context, so the DTO carries <c>Version</c>, the edit command carries it back
/// (<c>IVersionedEdit</c>), and the handler calls <c>db.ApplyLoadedVersion(entity, request.Version)</c>
/// before saving. A Modified or Deleted versioned entity that went through neither
/// <c>ApplyLoadedVersion</c> nor <c>UseCurrentVersion</c> fails the save: an omission is never a silent
/// last-writer-wins. See the README, "Concurrency".
/// </para>
/// <para>PostgreSQL only. On other providers the property is not mapped and the helpers throw.</para>
/// </remarks>
public interface IVersioned
{
    uint Version { get; }
}

/// <summary>An auditable entity with a concurrency token (Pass 59). See <see cref="IVersioned"/>.</summary>
public abstract class BaseVersionedEntity<TKey> : BaseAuditableEntity<TKey>, IVersioned
    where TKey : IEquatable<TKey>
{
    /// <summary>
    /// The row version (<c>xmin</c>). The setter is private so that no mapping from a DTO or a command can
    /// write it; only EF does, when it reads the row or reads back a save.
    /// </summary>
    public uint Version { get; private set; }
}

/// <inheritdoc cref="BaseVersionedEntity{TKey}"/>
public abstract class BaseVersionedEntity : BaseVersionedEntity<int>
{
}
