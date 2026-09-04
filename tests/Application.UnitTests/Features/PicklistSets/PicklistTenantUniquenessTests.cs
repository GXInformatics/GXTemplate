#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Features.PicklistSets;

/// <summary>
/// The uniqueness constraint on picklist values, which has to be per-tenant now that the rows are.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the defect Pass 24 predicted in writing and Pass 31 shipped anyway.</b>
/// <c>PicklistSetConfiguration</c> carried a unique index on <c>(Name, Value)</c> and a comment
/// saying: <i>"Whoever scopes picklists has to widen this index in the same change, or the first two
/// tenants to want the same brand name will collide on a constraint that has no business spanning
/// them."</i> Pass 31 scoped them and left the index alone.
/// </para>
/// <para>
/// <b>Pass 31's own tests did not catch it because they asserted the CHECK and never attempted the
/// WRITE.</b> <c>TheImportDuplicateCheckIsNowPerTenant</c> proves that the handler's
/// <c>AnyAsync(name &amp;&amp; value)</c> returns false for another tenant's value - which it does,
/// correctly, because the query filter hides that row. The insert that follows then hits a database
/// constraint the query could not see. The report and the README both went on to claim that two
/// tenants may import the same value; that claim was false at the storage layer.
/// </para>
/// <para>
/// The general shape is worth naming: <b>a query filter narrows what a query SEES, and a unique
/// index constrains what the table HOLDS.</b> Scoping reads does not scope constraints, and a
/// duplicate check written against the filtered view will disagree with the index every time the
/// hidden rows are the ones that matter.
/// </para>
/// </remarks>
[TestFixture]
public class PicklistTenantUniquenessTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

    private SqliteConnection _connection = null!;
    private DbContextOptions<ApplicationDbContext> _options = null!;

    private sealed class Ambient : IUserContextAccessor
    {
        private readonly UserContext? _context;
        public Ambient(string? tenantId) =>
            _context = tenantId is null ? null : new UserContext("u", "u", TenantId: tenantId);
        public UserContext? Current => _context;
        public IDisposable Push(UserContext context) => throw new NotSupportedException();
        public void Clear() { }
    }

    [SetUp]
    public async Task SetUp()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;

        await using var db = Context(null);
        await db.Database.EnsureCreatedAsync();
    }

    [TearDown]
    public async Task TearDown() => await _connection.DisposeAsync();

    private ApplicationDbContext Context(string? tenantId) => new(_options, new Ambient(tenantId));

    private static PicklistSet Row(string? tenantId) => new()
    {
        Name = Picklist.Brand,
        Value = "acme",
        Text = "Acme",
        TenantId = tenantId
    };

    [Test]
    public async Task TwoTenantsMayHoldTheSameNameAndValue()
    {
        // The behaviour Pass 31 decided, the README states, and the index prevented.
        await using (var a = Context(TenantA))
        {
            a.PicklistSets.Add(Row(TenantA));
            await a.SaveChangesAsync();
        }

        await using var b = Context(TenantB);
        b.PicklistSets.Add(Row(TenantB));

        var write = async () => await b.SaveChangesAsync();

        await write.Should().NotThrowAsync(
            "two tenants defining the same brand is the whole point of per-tenant additions; a " +
            "unique index spanning tenants makes the first one to use a value take it from everyone");
    }

    [Test]
    public async Task OneTenantStillCannotHoldTheSameNameAndValueTwice()
    {
        // NARROWED, NOT REMOVED. Widening the index to include the tenant must not turn it off:
        // duplicates within a tenant are what it exists to prevent, and they would render twice in
        // the same dropdown.
        await using var a = Context(TenantA);
        a.PicklistSets.AddRange(Row(TenantA), Row(TenantA));

        var write = async () => await a.SaveChangesAsync();

        await write.Should().ThrowAsync<DbUpdateException>(
            "a tenant may not define the same value twice");
    }

    [Test]
    public async Task TheSharedPartitionIsNotProtectedFromDuplicatesOnThisProvider()
    {
        // A KNOWN GAP, asserted so it cannot widen unnoticed rather than left to be discovered.
        //
        // Widening the index to (TenantId, Name, Value) protects each tenant's partition, but the
        // SHARED partition keys on a NULL - and SQLite and PostgreSQL treat NULLs as DISTINCT in a
        // unique index, so two shared rows with the same Name and Value do not collide. SQL Server
        // treats them as equal and does block it, so this is also a provider divergence.
        //
        // Closing it portably needs a second, PARTIAL unique index over (Name, Value) WHERE
        // TenantId IS NULL, whose filter SQL differs per provider. That was judged out of proportion
        // here: shared rows come from seeding, which is idempotent, or from a
        // PicklistSets.ManageShared holder who ALSO has no tenant - and both are narrow.
        //
        // This test runs on SQLite. If it ever starts failing, the gap has been closed and this
        // fixture should assert the protection instead of the gap.
        await using var db = Context(null);
        db.PicklistSets.AddRange(Row(null), Row(null));

        var write = async () => await db.SaveChangesAsync();

        await write.Should().NotThrowAsync(
            "SQLite treats NULLs as distinct, so the shared partition is unprotected - the gap this " +
            "test exists to name");
    }

    [Test]
    public async Task ATenantMayShadowNothing_TheDuplicateCheckAndTheIndexAgree()
    {
        // The pairing that failed before. The import handler asks AnyAsync over the FILTERED view;
        // the index constrains the WHOLE table. Where the two disagree, a check that says "not a
        // duplicate" is followed by an insert that says it is. After widening they agree: another
        // tenant's row is neither visible to the check nor blocking to the index.
        await using (var a = Context(TenantA))
        {
            a.PicklistSets.Add(Row(TenantA));
            await a.SaveChangesAsync();
        }

        await using var b = Context(TenantB);

        var checkSaysDuplicate = await b.PicklistSets
            .AnyAsync(x => x.Name == Picklist.Brand && x.Value == "acme");

        checkSaysDuplicate.Should().BeFalse("the filtered view cannot see tenant A's row");

        b.PicklistSets.Add(Row(TenantB));
        var write = async () => await b.SaveChangesAsync();

        await write.Should().NotThrowAsync(
            "and the index must agree with the check, or the import reports success and throws");
    }
}
