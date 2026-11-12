using CleanArchitecture.Blazor.Domain.Common.Entities;
using CleanArchitecture.Blazor.Domain.Common.Modules;
using CleanArchitecture.Blazor.Domain.Common.Precision;

namespace CleanArchitecture.Blazor.Domain.Modules.Sample.Entities;

// Pass 60c: the sample module proves the GX conventions in a generated project (plan §3.8). Warehouse is a deliberately
// common name: the template's own tests must collide with nothing an application calls its tables (T11).

/// <summary>Master data, int key, versioned: core."TBL_WAREHOUSES".</summary>
[Module<SampleModule>]
public class Warehouse : BaseVersionedEntity, IMustHaveBranch
{
    public int BranchId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int WarehouseTypeId { get; set; }
    public WarehouseStatus Status { get; set; } = WarehouseStatus.Open;
}

/// <summary>A lookup users pick from, seeded by the migration: core."TBL_LK_WAREHOUSE_TYPES" (id, name, slug).</summary>
[Module<SampleModule>]
public class WarehouseType : BaseLkEntity
{
    /// <summary>Seeded ids (the migration writes them explicitly; code may name them).</summary>
    public const int Main = 1, InTransit = 2;
}

/// <summary>An append-only ledger row, bigint key: core."TBL_WAREHOUSE_MOVEMENTS".</summary>
[Module<SampleModule>]
public class WarehouseMovement : BaseEntity<long>, IAppendOnly
{
    public int WarehouseId { get; set; }
    [Quantity] public decimal Quantity { get; set; }
    public string Note { get; set; } = string.Empty;
}

/// <summary>Stored as an integer, so every member has an explicit value (owner decision Q-H).</summary>
[ExplicitValues]
public enum WarehouseStatus
{
    Open = 1,
    Closed = 2
}
