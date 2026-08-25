#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Common.Interceptors;

/// <summary>
/// The transactional audit redesign. Audit rows used to travel a notification channel to a handler
/// that persisted them on a second context, which left a window between the business COMMIT and the
/// audit write in which a process kill lost the trail silently. They are now written in the same
/// transaction: either both are durable or neither is.
/// <para>
/// Every "is it durable" assertion here reads through a SEPARATE connection to the same database
/// file, because that is the only way to distinguish committed data from data merely staged on the
/// context under test.
/// </para>
/// </summary>
[TestFixture]
public class TransactionalAuditTests
{
    private const string ActingUserId = "audit-user";

    private string _dbPath = null!;
    private string _connectionString = null!;

    [SetUp]
    public async Task SetUp()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"gxaudit-{Guid.NewGuid():N}.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString();

        await using var ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();
        ctx.Users.Add(new ApplicationUser { Id = ActingUserId, UserName = "auditor", Email = "auditor@example.com" });
        await ctx.SaveChangesAsync();
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }

    // ---- harness -------------------------------------------------------------------------------

    private ApplicationDbContext CreateContext(params IInterceptor[] extra)
    {
        var userContext = new Mock<IUserContextAccessor>();
        userContext.SetupGet(x => x.Current).Returns(new UserContext(ActingUserId, "auditor"));
        var dateTime = new Mock<IDateTime>();
        dateTime.SetupGet(x => x.Now).Returns(new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc));

        var interceptors = new List<IInterceptor>
        {
            new AuditableEntityInterceptor(userContext.Object, dateTime.Object)
        };
        interceptors.AddRange(extra);

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connectionString)
            .AddInterceptors(interceptors)
            .Options;
        return new ApplicationDbContext(options);
    }

    /// <summary>Counts rows through an independent connection - i.e. only what is COMMITTED.</summary>
    private int CommittedCount(string sql, params (string name, object value)[] args)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private int CommittedPicklists(string name) =>
        CommittedCount("SELECT COUNT(*) FROM PicklistSets WHERE Value = $n", ("$n", name));

    private int CommittedAuditRows() =>
        CommittedCount("SELECT COUNT(*) FROM AuditTrails");

    private (string primaryKey, string changes, string auditType) CommittedAuditRow()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT PrimaryKey, Changes, AuditType FROM AuditTrails ORDER BY Id DESC LIMIT 1";
        using var r = cmd.ExecuteReader();
        r.Read();
        return (r.GetString(0), r.GetString(1), r.GetString(2));
    }

    private static PicklistSet NewPicklist(string value) => new()
    {
        Name = Picklist.Brand, Value = value, Text = value, Description = "audit test"
    };

    // ---- D1: atomicity ---------------------------------------------------------------------------

    [Test]
    public async Task Insert_CommitsTheEntityAndItsAuditRowTogether()
    {
        await using var ctx = CreateContext();
        ctx.PicklistSets.Add(NewPicklist("insert-me"));
        await ctx.SaveChangesAsync();

        CommittedPicklists("insert-me").Should().Be(1);
        CommittedAuditRows().Should().Be(1, "the audit row committed with the entity");
    }

    [Test]
    public async Task Insert_AuditRowCarriesTheRealResolvedKey()
    {
        await using var ctx = CreateContext();
        var product = NewPicklist("resolved-key");
        ctx.PicklistSets.Add(product);
        await ctx.SaveChangesAsync();

        product.Id.Should().BeGreaterThan(0);
        var row = CommittedAuditRow();
        row.primaryKey.Should().Contain($"\"{product.Id}\"",
            "writing audit rows during SavingChanges would have recorded a negative temporary sentinel");
        row.auditType.Should().Be(nameof(AuditType.Create));
    }

    [Test]
    public async Task Update_CommitsTheChangeAndItsAuditRowTogether()
    {
        int id;
        await using (var seed = CreateContext())
        {
            var p = NewPicklist("update-me");
            seed.PicklistSets.Add(p);
            await seed.SaveChangesAsync();
            id = p.Id;
        }
        var auditsAfterInsert = CommittedAuditRows();

        await using (var ctx = CreateContext())
        {
            var p = await ctx.PicklistSets.SingleAsync(x => x.Id == id);
            p.Value = "updated";
            await ctx.SaveChangesAsync();
        }

        CommittedPicklists("updated").Should().Be(1);
        CommittedAuditRows().Should().Be(auditsAfterInsert + 1);
        CommittedAuditRow().auditType.Should().Be(nameof(AuditType.Update));
    }

    [Test]
    public async Task Delete_CommitsTheRemovalAndItsAuditRowTogether()
    {
        int id;
        await using (var seed = CreateContext())
        {
            var p = NewPicklist("delete-me");
            seed.PicklistSets.Add(p);
            await seed.SaveChangesAsync();
            id = p.Id;
        }
        var auditsAfterInsert = CommittedAuditRows();

        await using (var ctx = CreateContext())
        {
            var p = await ctx.PicklistSets.SingleAsync(x => x.Id == id);
            ctx.PicklistSets.Remove(p);
            await ctx.SaveChangesAsync();
        }

        CommittedPicklists("delete-me").Should().Be(0);
        CommittedAuditRows().Should().Be(auditsAfterInsert + 1);
        CommittedAuditRow().auditType.Should().Be(nameof(AuditType.Delete));
    }

    // ---- D2: the ratified trade -------------------------------------------------------------------

    [Test]
    public async Task WhenTheAuditWriteFails_TheBusinessChangeRollsBackAndTheCallerIsTold()
    {
        // A dangling acting-user id violates AuditTrail's foreign key to AspNetUsers - the cheapest
        // faithful way to make only the audit write fail.
        var userContext = new Mock<IUserContextAccessor>();
        userContext.SetupGet(x => x.Current).Returns(new UserContext("no-such-user", "ghost"));
        var dateTime = new Mock<IDateTime>();
        dateTime.SetupGet(x => x.Now).Returns(new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc));

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connectionString)
            .AddInterceptors(new AuditableEntityInterceptor(userContext.Object, dateTime.Object))
            .Options;

        await using var ctx = new ApplicationDbContext(options);
        ctx.PicklistSets.Add(NewPicklist("doomed"));

        var act = async () => await ctx.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>("the audit failure must reach the caller");
        CommittedPicklists("doomed").Should().Be(0, "the business change rolled back with the audit row");
        CommittedAuditRows().Should().Be(0);
    }

    // ---- D3: re-entrancy --------------------------------------------------------------------------

    [Test]
    public async Task TheAuditWriteDoesNotRecurseOrAuditItself()
    {
        // Saving the audit rows re-enters the interceptor. Without the guard this is a stack overflow;
        // with it, exactly one audit row exists and there is no audit-of-the-audit.
        await using var ctx = CreateContext();
        ctx.PicklistSets.Add(NewPicklist("no-recursion"));
        await ctx.SaveChangesAsync();

        CommittedAuditRows().Should().Be(1, "one business change, one audit row - not two, not none");
    }

    // ---- D4: INV A1 closed ------------------------------------------------------------------------

    [Test]
    public async Task TwoContextsSharingOneInterceptor_BothAuditSetsLandIntact()
    {
        // The interceptor is registered scoped, so every context in a scope shares the instance. Its
        // pending trails used to live in a single field that each save overwrote; they are now keyed
        // to the context. Interleaving the two saves is what the old shape could not survive.
        var userContext = new Mock<IUserContextAccessor>();
        userContext.SetupGet(x => x.Current).Returns(new UserContext(ActingUserId, "auditor"));
        var dateTime = new Mock<IDateTime>();
        dateTime.SetupGet(x => x.Now).Returns(new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc));
        var shared = new AuditableEntityInterceptor(userContext.Object, dateTime.Object);

        ApplicationDbContext Make() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connectionString).AddInterceptors(shared).Options);

        await using var a = Make();
        await using var b = Make();

        a.PicklistSets.Add(NewPicklist("ctx-a"));
        b.PicklistSets.Add(NewPicklist("ctx-b"));

        await a.SaveChangesAsync();
        await b.SaveChangesAsync();

        CommittedPicklists("ctx-a").Should().Be(1);
        CommittedPicklists("ctx-b").Should().Be(1);
        CommittedAuditRows().Should().Be(2, "neither save's trails were clobbered by the other");
    }

    // ---- D5: INV A2 closed ------------------------------------------------------------------------

    [Test]
    public void SynchronousSaveChanges_AlsoProducesAuditRows()
    {
        // Only the async hooks were overridden, so a synchronous SaveChanges() audited nothing at all.
        using var ctx = CreateContext();
        ctx.PicklistSets.Add(NewPicklist("sync-save"));
        ctx.SaveChanges();

        CommittedPicklists("sync-save").Should().Be(1);
        CommittedAuditRows().Should().Be(1, "the synchronous path must audit too");
    }

    [Test]
    public void SynchronousSaveChanges_RollsBackWhenTheAuditWriteFails()
    {
        var userContext = new Mock<IUserContextAccessor>();
        userContext.SetupGet(x => x.Current).Returns(new UserContext("no-such-user", "ghost"));
        var dateTime = new Mock<IDateTime>();
        dateTime.SetupGet(x => x.Now).Returns(new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc));

        using var ctx = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connectionString)
            .AddInterceptors(new AuditableEntityInterceptor(userContext.Object, dateTime.Object))
            .Options);
        ctx.PicklistSets.Add(NewPicklist("sync-doomed"));

        var act = () => ctx.SaveChanges();

        act.Should().Throw<DbUpdateException>();
        CommittedPicklists("sync-doomed").Should().Be(0);
    }

    // ---- D6: JSON compatibility -------------------------------------------------------------------

    [Test]
    public async Task TheSerialisedChangesAndPrimaryKeyKeepTheirPreRedesignShape()
    {
        // The audit UI and export read these columns as opaque JSON, so the shape is a compatibility
        // contract. ResolveAuditTrails/CreateAuditTrail are unchanged from before the redesign; this
        // pins the observable result.
        await using var ctx = CreateContext();
        var product = NewPicklist("json-shape");
        ctx.PicklistSets.Add(product);
        await ctx.SaveChangesAsync();

        var row = CommittedAuditRow();

        row.primaryKey.Should().Be($"{{\"Id\":\"{product.Id}\"}}");
        row.changes.Should().StartWith("{").And.EndWith("}");
        // Property names keep their CLR casing; the AuditChange members are camel-cased by
        // JsonSerializerOptions.Web - exactly as before the redesign, since CreateAuditTrail and
        // ResolveAuditTrails are byte-for-byte unchanged from HEAD.
        row.changes.Should().Contain("\"Value\":{\"old\":null,\"new\":\"json-shape\"}");
        row.changes.Should().Contain("\"Text\":").And.Contain("\"Description\":");
        row.changes.Should().Contain("\"CreatedById\":{\"old\":null,\"new\":\"" + ActingUserId + "\"}");
        row.auditType.Should().Be(nameof(AuditType.Create), "AuditType is stored as its string name");
    }

    // ---- Tenant opt-in ----------------------------------------------------------------------------

    [Test]
    public async Task Tenant_IsAudited_AndItsAuditRowCommitsWithTheTenant()
    {
        // Tenant opted into IAuditable in Pass 7-2. It is the only audited entity that is not a
        // BaseAuditableEntity and whose key is a client-generated string, so it exercises a different
        // path through the key resolution than PicklistSet does.
        await using var ctx = CreateContext();
        var tenant = new Tenant { Name = "Contoso", Description = "audit test" };
        ctx.Tenants.Add(tenant);
        await ctx.SaveChangesAsync();

        CommittedCount("SELECT COUNT(*) FROM Tenants WHERE Name = $n", ("$n", "Contoso")).Should().Be(1);
        CommittedAuditRows().Should().Be(1, "the tenant's audit row committed with the tenant");

        var row = CommittedAuditRow();
        row.auditType.Should().Be(nameof(AuditType.Create));
        row.primaryKey.Should().Be($"{{\"Id\":\"{tenant.Id}\"}}",
            "the string key is recorded as written, not as a temporary sentinel");
        row.changes.Should().Contain("\"Name\":{\"old\":null,\"new\":\"Contoso\"}");
    }

    [Test]
    public async Task Tenant_AuditRowRollsBackWithTheTenantWhenTheAuditWriteFails()
    {
        var userContext = new Mock<IUserContextAccessor>();
        userContext.SetupGet(x => x.Current).Returns(new UserContext("no-such-user", "ghost"));
        var dateTime = new Mock<IDateTime>();
        dateTime.SetupGet(x => x.Now).Returns(new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc));

        await using var ctx = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connectionString)
            .AddInterceptors(new AuditableEntityInterceptor(userContext.Object, dateTime.Object))
            .Options);
        ctx.Tenants.Add(new Tenant { Name = "doomed-tenant" });

        var act = async () => await ctx.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
        CommittedCount("SELECT COUNT(*) FROM Tenants WHERE Name = $n", ("$n", "doomed-tenant")).Should().Be(0);
        CommittedAuditRows().Should().Be(0);
    }

    // ---- no transaction hijack --------------------------------------------------------------------

    [Test]
    public async Task WhenTheCallerAlreadyOwnsATransaction_TheInterceptorJoinsItRatherThanOpeningAnother()
    {
        await using var ctx = CreateContext();
        await using var tx = await ctx.Database.BeginTransactionAsync();

        ctx.PicklistSets.Add(NewPicklist("caller-tx"));
        await ctx.SaveChangesAsync();

        CommittedPicklists("caller-tx").Should().Be(0, "the caller's transaction is still open");
        CommittedAuditRows().Should().Be(0, "the audit row is inside it too");

        await tx.CommitAsync();

        CommittedPicklists("caller-tx").Should().Be(1);
        CommittedAuditRows().Should().Be(1, "both became durable at the caller's commit");
    }
}
#nullable restore
