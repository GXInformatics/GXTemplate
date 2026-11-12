using CleanArchitecture.Blazor.Domain.Modules.Sample.Entities;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Extensions;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CleanArchitecture.Blazor.Infrastructure.Modules.Sample.Configurations;

// Pass 60c (plan §3.8). Nothing here names a table or a column: the convention puts each in core, plural, snake_case.
// The two constraints named by hand follow decision Q-E (ux_tbl_, ck_tbl_), and the check constraint's SQL names its
// column through GxSql.Column, so it follows the snake_case name.

public class WarehouseConfiguration : IEntityTypeConfiguration<Warehouse>
{
    public void Configure(EntityTypeBuilder<Warehouse> builder)
    {
        builder.Property(w => w.Code).HasMaxLength(10).IsRequired();
        builder.Property(w => w.Name).HasMaxLength(100).IsRequired();
        builder.HasIndex(w => new { w.BranchId, w.Code }).IsUnique().HasDatabaseName("ux_tbl_warehouses_branch_code");
        builder.ToTable(t => t.HasCheckConstraint("ck_tbl_warehouses_code_upper",
            $"{GxSql.Column(nameof(Warehouse.Code))} = upper({GxSql.Column(nameof(Warehouse.Code))})"));
        builder.HasOne<Branch>().WithMany().HasForeignKey(w => w.BranchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<WarehouseType>().WithMany().HasForeignKey(w => w.WarehouseTypeId).OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(w => w.DomainEvents);
    }
}

public class WarehouseTypeConfiguration : IEntityTypeConfiguration<WarehouseType>
{
    public void Configure(EntityTypeBuilder<WarehouseType> builder)
    {
        builder.Property(t => t.Name).HasMaxLength(100).IsRequired();
        builder.Property(t => t.Slug).HasMaxLength(100);
        builder.Ignore(t => t.DomainEvents);
    }
}

public class WarehouseMovementConfiguration : IEntityTypeConfiguration<WarehouseMovement>
{
    public void Configure(EntityTypeBuilder<WarehouseMovement> builder)
    {
        builder.Property(m => m.Note).HasMaxLength(200);
        builder.HasOne<Warehouse>().WithMany().HasForeignKey(m => m.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(m => m.DomainEvents);
    }
}

/// <summary>
/// The module's tightening of the template's Branch (README "Extending branches"; T9): names are unique. A
/// configuration of the template's entity that adds only the index - the template's own configuration still applies.
/// </summary>
public class SampleBranchNameIndex : IEntityTypeConfiguration<Branch>
{
    public void Configure(EntityTypeBuilder<Branch> builder) => builder.HasIndex(b => b.Name).IsUnique();
}
