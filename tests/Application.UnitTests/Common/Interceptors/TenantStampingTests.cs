#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Npgsql;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Common.Interceptors;

/// <summary>
/// Tenant stamping on the business entities that carry a tenant: the rows record which tenant they
/// were written in, and they record it at write time.
/// </summary>
/// <remarks>
/// <b>Stamping, not scoping.</b> Nothing in the application filters on any of these columns yet, and
/// nothing here asserts that it does. What is asserted is that the value is present, correct, and
/// permanent - because the column has to be right before anything is allowed to depend on it, and
/// because it is far cheaper to get right now than after a customer database exists.
/// <para>
/// Durability is read through a SEPARATE connection to the same database, following
/// <c>TransactionalAuditTests</c>: only that distinguishes a committed row from one staged on the
/// context under test.
/// </para>
/// </remarks>
[TestFixture]
public class TenantStampingTests
{
    private const string ActingUserId = "stamp-user";
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

    private string _connectionString = null!;

    [SetUp]
    public async Task SetUp()
    {
        // The shared test database, emptied, in place of a new database file per test.
        _connectionString = UnitTestDatabase.ConnectionString;
        await UnitTestDatabase.ResetAsync();

        await using var ctx = CreateContext(tenantId: null);
        ctx.Tenants.Add(new Tenant { Id = TenantA, Name = "Tenant A" });
        ctx.Tenants.Add(new Tenant { Id = TenantB, Name = "Tenant B" });
        ctx.Users.Add(new ApplicationUser
        {
            Id = ActingUserId, UserName = "stamper", Email = "stamper@example.com", TenantId = TenantA
        });
        await ctx.SaveChangesAsync();
    }

    // ---- harness -------------------------------------------------------------------------------

    /// <summary>
    /// A context whose ambient principal is in <paramref name="tenantId"/>, or - when that is
    /// <c>null</c> - a context with no ambient principal at all, which is what seeding, startup
    /// provisioning and background work actually look like.
    /// </summary>
    private ApplicationDbContext CreateContext(string? tenantId)
    {
        var userContext = new Mock<IUserContextAccessor>();
        userContext.SetupGet(x => x.Current).Returns(
            tenantId is null ? null : new UserContext(ActingUserId, "stamper", TenantId: tenantId));

        var dateTime = new Mock<IDateTime>();
        dateTime.SetupGet(x => x.UtcNow).Returns(new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc));

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(_connectionString)
            .AddInterceptors(new AuditableEntityInterceptor(userContext.Object, dateTime.Object))
            .Options;
        return new ApplicationDbContext(options);
    }

    /// <summary>Reads one committed scalar through an independent connection.</summary>
    private object? CommittedScalar(string sql)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var value = cmd.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    private string? CommittedAuditTenant() =>
        (string?)CommittedScalar("SELECT \"TenantId\" FROM \"AuditTrails\" ORDER BY \"Id\" DESC LIMIT 1");

    private string? CommittedPicklistTenant(string value) =>
        (string?)CommittedScalar($"SELECT \"TenantId\" FROM \"PicklistSets\" WHERE \"Value\" = '{value}'");

    private static PicklistSet NewPicklist(string value) => new()
    {
        Name = Picklist.Brand, Value = value, Text = value, Description = "stamping test"
    };

    // ---- with a tenant context -----------------------------------------------------------------

    [Test]
    public async Task APicklistWrittenInsideATenant_RecordsThatTenant()
    {
        await using var ctx = CreateContext(TenantA);
        ctx.PicklistSets.Add(NewPicklist("brand-a"));
        await ctx.SaveChangesAsync();

        CommittedPicklistTenant("brand-a").Should().Be(TenantA);
    }

    [Test]
    public async Task TheAuditRowForAChangeMadeInsideATenant_RecordsThatTenant()
    {
        await using var ctx = CreateContext(TenantA);
        ctx.PicklistSets.Add(NewPicklist("audited-a"));
        await ctx.SaveChangesAsync();

        CommittedAuditTenant().Should().Be(TenantA, "the audit row belongs to the tenant the change was made in");
    }

    [Test]
    public async Task ADocumentWrittenInsideATenant_StillRecordsThatTenant()
    {
        // Document has carried IMayHaveTenant since long before this pass. It is here so that the
        // one entity whose stamping was already working is measured by the same test as the two
        // that have just started - otherwise a regression in the older path has no owner.
        await using var ctx = CreateContext(TenantA);
        ctx.Documents.Add(new Document { Title = "doc-a", DocumentType = DocumentType.Document });
        await ctx.SaveChangesAsync();

        CommittedScalar("SELECT \"TenantId\" FROM \"Documents\" WHERE \"Title\" = 'doc-a'").Should().Be(TenantA);
    }

    // ---- with no context at all ----------------------------------------------------------------

    [Test]
    public async Task AWriteWithNoAmbientPrincipal_LeavesTheTenantNull_RatherThanFailing()
    {
        // Seeding and startup provisioning run exactly like this. A null tenant is the correct
        // record for them - they belong to the installation - and the important half of the claim
        // is that the save SUCCEEDS: a non-null constraint here would have broken first boot.
        await using var ctx = CreateContext(tenantId: null);
        ctx.PicklistSets.Add(NewPicklist("brand-none"));

        var act = async () => await ctx.SaveChangesAsync();

        await act.Should().NotThrowAsync();
        CommittedPicklistTenant("brand-none").Should().BeNull();
        CommittedAuditTenant().Should().BeNull("an installation-level change belongs to no tenant");
    }

    // ---- the reason the column exists at all ---------------------------------------------------

    [Test]
    public async Task AnAuditRowsTenantSurvivesItsAuthorMovingTenant()
    {
        // The whole justification for storing the tenant rather than joining to it later.
        // TenantSwitchService writes ApplicationUser.TenantId in place, so a report that derived an
        // audit row's tenant from its author would re-attribute every historical row the moment
        // somebody switched. This asserts the stored value does not move.
        await using (var ctx = CreateContext(TenantA))
        {
            ctx.PicklistSets.Add(NewPicklist("history"));
            await ctx.SaveChangesAsync();
        }

        CommittedAuditTenant().Should().Be(TenantA);

        // The author moves to tenant B, exactly as the tenant switcher would move them.
        await using (var ctx = CreateContext(TenantA))
        {
            var user = await ctx.Users.FindAsync(ActingUserId);
            user!.TenantId = TenantB;
            await ctx.SaveChangesAsync();
        }

        CommittedScalar($"SELECT \"TenantId\" FROM \"AspNetUsers\" WHERE \"Id\" = '{ActingUserId}'")
            .Should().Be(TenantB, "the user really did move");

        CommittedScalar(
                "SELECT \"TenantId\" FROM \"AuditTrails\" WHERE \"TableName\" = 'PicklistSet' ORDER BY \"Id\" LIMIT 1")
            .Should().Be(TenantA, "history records where the change happened, not where its author is now");
    }
}

/// <summary>
/// The tenant columns themselves, asserted on the EF model: the migrations are generated from it and
/// <c>ModelMatchesMigrations</c> holds them to it.
/// </summary>
/// <remarks>
/// A separate fixture because it needs no database (pass 47; these read a SQLite file database's pragma tables
/// before). Under <see cref="TenantStampingTests"/>'s <c>[SetUp]</c>, any model change that breaks
/// them fails first in the shared database's migration step, so these assertions would never be the
/// ones that report it.
/// </remarks>
[TestFixture]
public class TenantColumnSchemaTests
{
    private static ApplicationDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql("Host=none").Options);

    [Test]
    public void TheTenantColumnsExistAndAreNullable()
    {
        // Nullable is not a detail: a principal-less write is a supported state, and a NOT NULL
        // column would turn every seeding write into a startup failure.
        using var ctx = CreateContext();
        foreach (var entity in new[] { typeof(AuditTrail), typeof(PicklistSet), typeof(Document) })
        {
            var tenantId = ctx.Model.FindEntityType(entity)!.FindProperty("TenantId");

            tenantId.Should().NotBeNull($"{entity.Name}.TenantId should exist");
            tenantId!.IsNullable.Should().BeTrue($"{entity.Name}.TenantId must be nullable");
        }
    }

    /// <summary>
    /// No foreign key from AuditTrails to Tenants, deliberately.
    /// </summary>
    /// <remarks>
    /// Every delete behaviour available to a relationship is wrong for an audit row: Cascade erases
    /// the trail of the tenant that was just deleted, Restrict makes deleting an audited tenant
    /// impossible, and SetNull rewrites history to say the change belonged to nobody. Stated as a
    /// test because "we chose not to add a constraint" is otherwise indistinguishable from "we
    /// forgot to".
    /// </remarks>
    [Test]
    public void TheAuditTrailsTenantColumnCarriesNoForeignKey()
    {
        using var ctx = CreateContext();
        var auditTrail = ctx.Model.FindEntityType(typeof(AuditTrail))!;

        auditTrail.GetForeignKeys()
            .Should().NotContain(fk => fk.Properties.Any(p => p.Name == "TenantId"),
                "every delete behaviour a relationship could have is wrong for an audit row");
    }
}
#nullable restore
