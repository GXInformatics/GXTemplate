#nullable enable
using System;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using Npgsql;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.MultiTenant;

/// <summary>
/// Pass 54: tenant stamping follows the MARKER, not <c>IAuditableEntity</c>.
/// </summary>
/// <remarks>
/// <see cref="TenantProbe"/> is a plain <c>BaseEntity</c>. Before this pass the interceptor stamped
/// tenants only inside its <c>IAuditableEntity</c> loop, so a probe inserted under a principal kept
/// its null tenant and the insert failed on the NOT NULL column. Values are read back through an
/// independent connection, as <c>TenantStampingTests</c> does, so they are what was committed.
/// </remarks>
[TestFixture]
public class TenantStampingByMarkerTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private string _connectionString = null!;

    [SetUp]
    public async Task SetUp()
    {
        _connectionString = UnitTestDatabase.ConnectionString;
        await UnitTestDatabase.ResetAsync();
        await TenantProbeDbContext.EnsureTableAsync(_connectionString);
    }

    [OneTimeTearDown]
    public Task DropProbeTable() => TenantProbeDbContext.DropAsync(UnitTestDatabase.ConnectionString);

    private TenantProbeDbContext Context(string? tenantId)
    {
        var accessor = new Mock<IUserContextAccessor>();
        accessor.SetupGet(x => x.Current).Returns(
            tenantId is null ? null : new UserContext("stamper", "stamper", TenantId: tenantId));
        var dateTime = new Mock<IDateTime>();
        dateTime.SetupGet(x => x.UtcNow).Returns(new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc));

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(_connectionString)
            .AddInterceptors(new AuditableEntityInterceptor(accessor.Object, dateTime.Object))
            .Options;
        return new TenantProbeDbContext(options, accessor.Object);
    }

    private string? CommittedTenantOf(string note)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT \"TenantId\" FROM {TenantProbeDbContext.Schema}.\"TenantProbes\" WHERE \"Note\" = @n";
        cmd.Parameters.AddWithValue("n", note);
        return cmd.ExecuteScalar() as string;
    }

    [Test]
    public async Task APlainMustHaveTenantRow_InsertedUnderAPrincipal_GetsThatPrincipalsTenant()
    {
        await using var db = Context(TenantA);
        db.Probes.Add(new TenantProbe { Note = "stamped" });
        await db.SaveChangesAsync();

        CommittedTenantOf("stamped").Should().Be(TenantA);
    }

    [Test]
    public async Task AnExplicitTenant_IsNotOverwritten_WhichIsHowASeederWritesForItsTenant()
    {
        // The tenant seeder shape: no principal, the tenant set by hand.
        await using (var seeding = Context(tenantId: null))
        {
            seeding.Probes.Add(new TenantProbe { Note = "seeded", TenantId = TenantB });
            await seeding.SaveChangesAsync();
        }

        // And under a principal in another tenant, the explicit value still stands - stamping fills
        // an omission; it does not second-guess a decision.
        await using (var signedIn = Context(TenantA))
        {
            signedIn.Probes.Add(new TenantProbe { Note = "explicit", TenantId = TenantB });
            await signedIn.SaveChangesAsync();
        }

        CommittedTenantOf("seeded").Should().Be(TenantB);
        CommittedTenantOf("explicit").Should().Be(TenantB);
    }

    [Test]
    public async Task AMustHaveTenantRow_WithNoTenantAndNoPrincipal_IsRefused_NamingTheType()
    {
        await using var db = Context(tenantId: null);
        db.Probes.Add(new TenantProbe { Note = "orphan" });

        var save = () => db.SaveChangesAsync();

        // Refused by the interceptor, before the database is asked - not a NOT NULL violation.
        await save.Should().ThrowAsync<InvalidOperationException>().WithMessage("*TenantProbe*must have a tenant*");
        CommittedTenantOf("orphan").Should().BeNull();
    }

    [Test]
    public async Task AuditableEntities_AreStillStamped_ThroughTheSamePass()
    {
        // The pre-pass path, moved: Document is IAuditableEntity AND IMayHaveTenant. (Unlike the
        // probe, Document has foreign keys to its tenant and its author, so both have to exist.)
        await using (var setup = Context(tenantId: null))
        {
            setup.Tenants.Add(new Tenant { Id = TenantA, Name = "A" });
            setup.Users.Add(new ApplicationUser { Id = "stamper", UserName = "stamper", Email = "stamper@example.com" });
            await setup.SaveChangesAsync();
        }

        await using var db = Context(TenantA);
        db.Documents.Add(new Document { Title = "doc", DocumentType = DocumentType.Document });
        await db.SaveChangesAsync();

        (await db.Documents.SingleAsync()).TenantId.Should().Be(TenantA);
    }
}
#nullable restore
