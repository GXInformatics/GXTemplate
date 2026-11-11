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

        // "Exactly one default per user with any assignment" is SetUserBranchesCommand's rule, the only
        // writer. Pass 60b: on PostgreSQL, the only target, a partial unique index also keeps "at most one"
        // (ApplicationDbContext.UserBranchDefaultIndex). It is not declared here because a boolean filter has
        // no spelling all three providers accept (SQL Server has no TRUE literal, PostgreSQL will not compare
        // a boolean with 1), and the template still builds all three migrations.
        builder.HasIndex(ub => ub.BranchId);
    }
}

/// <summary>Approval limits (Pass 60). A template table, in the default schema.</summary>
public class ApprovalLimitConfiguration : IEntityTypeConfiguration<ApprovalLimit>
{
    public void Configure(EntityTypeBuilder<ApprovalLimit> builder)
    {
        builder.ToTable("ApprovalLimits");
        builder.Property(l => l.RoleId).HasMaxLength(450).IsRequired();
        builder.Property(l => l.DocumentType).HasMaxLength(64).IsRequired();

        // Restrict both ways: a role or a branch with limits is not deleted out from under them.
        builder.HasOne<CleanArchitecture.Blazor.Domain.Identity.ApplicationRole>().WithMany()
            .HasForeignKey(l => l.RoleId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Branch>().WithMany().HasForeignKey(l => l.BranchId).OnDelete(DeleteBehavior.Restrict);

        // The policy's lookup. "One row per (role, document type, branch)" is kept by the maintenance command, not a
        // unique index: a nullable BranchId has no unique-index spelling all three providers accept (pass 59 met the
        // same problem for UserBranches).
        builder.HasIndex(l => new { l.DocumentType, l.RoleId, l.BranchId });
        builder.Ignore(l => l.DomainEvents);
    }
}
