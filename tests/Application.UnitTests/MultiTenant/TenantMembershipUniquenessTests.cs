#nullable enable
using System.Linq;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.MultiTenant;

/// <summary>
/// Pass 54: one <see cref="TenantUser"/> row per (tenant, user), enforced by the database through
/// the migrated unique index rather than by every writer remembering to check.
/// </summary>
[TestFixture]
public class TenantMembershipUniquenessTests
{
    private ApplicationDbContext Context() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(UnitTestDatabase.ConnectionString).Options);

    [SetUp]
    public async Task SetUp()
    {
        await UnitTestDatabase.ResetAsync();
        await using var db = Context();
        db.Tenants.AddRange(new Tenant { Id = "t1", Name = "One" }, new Tenant { Id = "t2", Name = "Two" });
        db.Users.Add(new ApplicationUser { Id = "u1", UserName = "u1", Email = "u1@example.com" });
        db.TenantUsers.Add(new TenantUser { TenantId = "t1", UserId = "u1" });
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task ASecondMembershipOfTheSameUserInTheSameTenant_IsRefusedByTheDatabase()
    {
        await using var db = Context();
        db.TenantUsers.Add(new TenantUser { TenantId = "t1", UserId = "u1" });

        var save = () => db.SaveChangesAsync();

        await save.Should().ThrowAsync<DbUpdateException>();
    }

    [Test]
    public async Task TheSameUserInAnotherTenant_IsStillAllowed()
    {
        await using var db = Context();
        db.TenantUsers.Add(new TenantUser { TenantId = "t2", UserId = "u1" });
        await db.SaveChangesAsync();

        (await db.TenantUsers.CountAsync(tu => tu.UserId == "u1")).Should().Be(2);
    }

    [Test]
    public async Task RewritingAUsersMemberships_InOneSave_StillWorks()
    {
        // UserFormDialog's edit path, exactly: delete every membership row of the user and add the
        // selected ones back in the SAME SaveChanges - so the re-added (t1, u1) coexists with the
        // deleted (t1, u1) until the batch runs. The index is only safe there if the delete is sent
        // first, which EF orders for a unique index; this is what holds it to that.
        await using (var db = Context())
        {
            db.TenantUsers.RemoveRange(await db.TenantUsers.Where(tu => tu.UserId == "u1").ToListAsync());
            db.TenantUsers.Add(new TenantUser { TenantId = "t1", UserId = "u1" });
            db.TenantUsers.Add(new TenantUser { TenantId = "t2", UserId = "u1" });
            await db.SaveChangesAsync();
        }

        await using var check = Context();
        (await check.TenantUsers.Where(tu => tu.UserId == "u1").Select(tu => tu.TenantId).OrderBy(t => t).ToListAsync())
            .Should().Equal("t1", "t2");
    }

    [Test]
    public void TheModelDeclaresTheIndex_TenantFirst()
    {
        using var db = Context();
        var index = db.Model.FindEntityType(typeof(TenantUser))!.GetIndexes()
            .Single(i => i.IsUnique);

        index.Properties.Select(p => p.Name).Should().Equal(nameof(TenantUser.TenantId), nameof(TenantUser.UserId));
    }
}
#nullable restore
