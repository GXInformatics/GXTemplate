namespace CleanArchitecture.Blazor.Domain.Common.Entities;

/// <summary>
/// A lookup with MNEFleets' fixed columns: <c>Id</c> (int), <c>Name</c>, <c>Slug</c> (Pass 60c, owner decisions D4
/// and Q-A3). Its table is <c>core."TBL_LK_…"</c>; see <see cref="ILookupEntity"/>.
/// </summary>
/// <remarks>
/// <code>[Module&lt;CatalogueModule&gt;] public class VehicleMake : BaseLkEntity { }   // core."TBL_LK_VEHICLE_MAKES"</code>
/// A lookup users edit, and which therefore needs the version check and the audit columns, derives from
/// <see cref="BaseVersionedLkEntity"/> instead.
/// </remarks>
public abstract class BaseLkEntity : BaseEntity<int>, ILookupEntity
{
    /// <inheritdoc />
    public string Name { get; set; } = "";

    /// <inheritdoc />
    public string? Slug { get; set; }
}

/// <summary>
/// A lookup users maintain: <see cref="BaseLkEntity"/>'s fixed columns on <see cref="BaseVersionedEntity{TKey}"/>, so
/// edits carry the version the user loaded and the audit columns are stamped (Pass 60c).
/// </summary>
public abstract class BaseVersionedLkEntity : BaseVersionedEntity<int>, ILookupEntity
{
    /// <inheritdoc />
    public string Name { get; set; } = "";

    /// <inheritdoc />
    public string? Slug { get; set; }
}
