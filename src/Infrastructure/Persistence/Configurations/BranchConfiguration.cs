using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CleanArchitecture.Blazor.Infrastructure.Persistence.Configurations;

/// <summary>Branches (Pass 59). A template table, so it stays out of the <c>core</c> schema.</summary>
public class BranchConfiguration : IEntityTypeConfiguration<Branch>
{
    public void Configure(EntityTypeBuilder<Branch> builder)
    {
        builder.ToTable("Branches");
        builder.Property(b => b.Code).HasMaxLength(20).IsRequired();
        builder.Property(b => b.Name).HasMaxLength(100).IsRequired();
        builder.HasIndex(b => b.Code).IsUnique();
        builder.Ignore(b => b.DomainEvents);
    }
}

/// <summary>User-to-branch assignments (Pass 59).</summary>
public class UserBranchConfiguration : IEntityTypeConfiguration<UserBranch>
{
    public void Configure(EntityTypeBuilder<UserBranch> builder)
    {
        builder.ToTable("UserBranches");
        builder.HasKey(ub => new { ub.UserId, ub.BranchId });

        // Restrict both ways: a user is deactivated and a branch is deactivated; neither is deleted, so
        // nothing should ever cascade an assignment away.
        builder.HasOne(ub => ub.User).WithMany().HasForeignKey(ub => ub.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(ub => ub.Branch).WithMany().HasForeignKey(ub => ub.BranchId).OnDelete(DeleteBehavior.Restrict);

        // "One default per user" is kept by SetUserBranchesCommand, the only writer, not by a filtered
        // unique index: a boolean filter has no spelling all three providers accept (SQL Server has no
        // TRUE literal, PostgreSQL will not compare a boolean with 1), and the template still builds all
        // three migrations.
        builder.HasIndex(ub => ub.BranchId);
    }
}
