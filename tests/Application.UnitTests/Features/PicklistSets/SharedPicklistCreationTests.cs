#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Mappings;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Application.Features.PicklistSets;
using CleanArchitecture.Blazor.Application.Features.PicklistSets.Commands.AddEdit;
using CleanArchitecture.Blazor.Domain.Common.Entities;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Features.PicklistSets;

/// <summary>
/// A tenant-scoped <c>PicklistSets.ManageShared</c> holder creating an INSTALLATION-WIDE row - the
/// gap Pass 32 §2.5 found and left.
/// </summary>
/// <remarks>
/// <para>
/// <b>The obstacle was subtler than "the interceptor stamps unconditionally".</b> It does not:
/// <c>SetCreationAuditInfo</c> already stamped only when <c>TenantId</c> was null. The problem is
/// that null is the sentinel for "not set yet" AND the value that means "shared", so a tenant-scoped
/// principal had no way to say which they meant. <c>IMayBeShared.CreateAsShared</c> is that
/// distinction and nothing else.
/// </para>
/// <para>
/// <b>Every assertion runs the REAL interceptor and reads the stored tenant back.</b> Pass 32 A2
/// recorded that the handler's "the tenant this row will carry" and the interceptor's stamping are
/// two copies of one rule, and this pass ties them tighter: the handler now sets the flag the
/// interceptor reads. Asserting the created row's tenant through a no-principal context is what
/// checks the two copies against each other rather than assuming they agree.
/// </para>
/// <para>
/// <b>Containment is asserted, not asserted-about.</b>
/// <see cref="NothingElseInTheDomainCanOptOutOfStamping"/> and
/// <see cref="TheFlagNeverReachesTheDatabase"/> are the tests that keep this from becoming a general
/// escape from Pass 24's stamping rule.
/// </para>
/// </remarks>
[TestFixture]
public class SharedPicklistCreationTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private const string Holder = "user-holder";
    private const string NonHolder = "user-non-holder";

    private SqliteConnection _connection = null!;

    private sealed class Ambient : IUserContextAccessor
    {
        public Ambient(string? userId, string? tenantId) =>
            Current = userId is null ? null : new UserContext(userId, userId, TenantId: tenantId);
        public UserContext? Current { get; private set; }
        public IDisposable Push(UserContext context) => throw new NotSupportedException();
        public void Clear() => Current = null;
    }

    /// <summary>
    /// Answers for one named holder and nobody else, returning an UNASSIGNED row for the non-holder
    /// rather than an empty list - <c>Assigned</c> is the field the rule reads.
    /// </summary>
    private sealed class Permissions : IPermissionQueryService
    {
        public Task<IList<PermissionModel>> GetAllPermissionsByUserId(string userId)
        {
            IList<PermissionModel> held =
            [
                new PermissionModel
                {
                    ClaimType = ApplicationClaimTypes.Permission,
                    ClaimValue = Application.Common.Security.Permissions.PicklistSets.ManageShared,
                    Assigned = string.Equals(userId, Holder, StringComparison.Ordinal)
                }
            ];
            return Task.FromResult(held);
        }

        public Task<IList<PermissionModel>> GetAllPermissionsByRoleId(string roleId) =>
            Task.FromResult<IList<PermissionModel>>([]);
    }

    private sealed class Factory : IApplicationDbContextFactory
    {
        private readonly SqliteConnection _connection;
        private readonly IUserContextAccessor _accessor;

        public Factory(SqliteConnection connection, IUserContextAccessor accessor)
        {
            _connection = connection;
            _accessor = accessor;
        }

        public ValueTask<IApplicationDbContext> CreateAsync(CancellationToken ct = default) =>
            new(Build(_connection, _accessor));

        public static ApplicationDbContext Build(SqliteConnection connection, IUserContextAccessor accessor)
        {
            var dateTime = new Mock<IDateTime>();
            dateTime.SetupGet(x => x.UtcNow).Returns(new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc));

            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection)
                .AddInterceptors(new AuditableEntityInterceptor(accessor, dateTime.Object))
                .Options;

            return new ApplicationDbContext(options, accessor);
        }
    }

    private static readonly IUserContextAccessor NoPrincipal = new Ambient(userId: null, tenantId: null);

    [SetUp]
    public async Task SetUp()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        await using var db = Factory.Build(_connection, NoPrincipal);
        await db.Database.EnsureCreatedAsync();

        // Pass 32 A5: the interceptor's audit row has a real FK to AspNetUsers, so without these
        // every SUCCESSFUL write here fails on the constraint and only the refusals pass - a
        // fixture that is entirely green while proving nothing works.
        db.Users.AddRange(
            new ApplicationUser { Id = Holder, UserName = Holder, Email = $"{Holder}@example.test" },
            new ApplicationUser { Id = NonHolder, UserName = NonHolder, Email = $"{NonHolder}@example.test" });
        await db.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown() => await _connection.DisposeAsync();

    private AddEditPicklistSetCommandHandler AddEdit(string? userId, string? tenantId)
    {
        var accessor = new Ambient(userId, tenantId);
        return new AddEditPicklistSetCommandHandler(
            new Factory(_connection, accessor),
            new MapsterObjectMapper(MapsterConfiguration.Create()),
            new Permissions(),
            accessor);
    }

    private static AddEditPicklistSetCommand Create(string value, bool isShared) => new()
    {
        Name = Picklist.Brand,
        Value = value,
        Text = value,
        IsShared = isShared
    };

    /// <summary>
    /// Reads the row as STORED, filters off.
    /// </summary>
    /// <remarks>
    /// <b><c>IgnoreQueryFilters</c> is required, and the first draft without it failed usefully.</b>
    /// A no-principal context is not an unfiltered one: <c>PicklistSet</c>'s filter is
    /// <c>TenantId == null || TenantId == CurrentTenantId</c>, and with no principal
    /// <c>CurrentTenantId</c> is null - so it admits SHARED rows only, and every private row this
    /// fixture creates was invisible to its own assertion. A readback that can only see the value
    /// the test hopes for is not a readback.
    /// </remarks>
    private async Task<PicklistSet?> StoredAsync(string value)
    {
        await using var db = Factory.Build(_connection, NoPrincipal);
        return await db.PicklistSets.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(p => p.Value == value);
    }

    // ---- the gap, closed -----------------------------------------------------------------------

    [Test]
    public async Task ATenantScopedHolderCanNowCreateASharedRow()
    {
        // The whole point. Before this pass the row came back stamped 'tenant-a' whatever the
        // caller asked for, and Pass 31 could truthfully say shared rows came only from seeding.
        var result = await AddEdit(Holder, TenantA)
            .Handle(Create("installation-wide", isShared: true), default);

        result.Succeeded.Should().BeTrue(result.ErrorMessage);
        (await StoredAsync("installation-wide"))!.TenantId.Should().BeNull();
    }

    [Test]
    public async Task TheSharedRowIsThenVisibleToAnotherTenant()
    {
        // "Shared" is a claim about who can SEE it, so it is checked through the query filter of a
        // principal in a different tenant rather than by re-reading the column.
        await AddEdit(Holder, TenantA).Handle(Create("installation-wide", isShared: true), default);

        await using var asTenantB = Factory.Build(_connection, new Ambient(NonHolder, TenantB));
        (await asTenantB.PicklistSets.Where(p => p.Value == "installation-wide").ToListAsync())
            .Should().HaveCount(1, "a shared row is visible to every tenant, which is what it means");
    }

    [Test]
    public async Task ATenantScopedNonHolderAskingForSharedIsRefusedAndWritesNothing()
    {
        var result = await AddEdit(NonHolder, TenantA)
            .Handle(Create("attempted-shared", isShared: true), default);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Be(SharedPicklistWrite.Refused);
        (await StoredAsync("attempted-shared")).Should().BeNull(
            "the flag is a request, not a grant - a refused create leaves no row at all, shared or private");
    }

    // ---- narrowed, not emptied ------------------------------------------------------------------

    [Test]
    public async Task AHolderNotAskingForSharedStillCreatesAPrivateRow()
    {
        // The default did not move. A holder's ordinary create is still their own tenant's row, so
        // the new capability is opt-in rather than a change of meaning for every existing caller.
        var result = await AddEdit(Holder, TenantA).Handle(Create("a-only", isShared: false), default);

        result.Succeeded.Should().BeTrue(result.ErrorMessage);
        (await StoredAsync("a-only"))!.TenantId.Should().Be(TenantA);
    }

    [Test]
    public async Task ANonHolderStillCreatesTheirOwnTenantsRow()
    {
        var result = await AddEdit(NonHolder, TenantA).Handle(Create("a-private", isShared: false), default);

        result.Succeeded.Should().BeTrue(result.ErrorMessage);
        (await StoredAsync("a-private"))!.TenantId.Should().Be(TenantA);
    }

    [Test]
    public async Task ATenantlessHolderStillCreatesASharedRowWithoutAskingForOneExplicitly()
    {
        // Unchanged from Pass 32: a caller with no tenant produces a shared row either way. Kept so
        // that a future "require IsShared explicitly" change is a visible decision rather than a
        // silent regression for the seeding path.
        var result = await AddEdit(Holder, tenantId: null)
            .Handle(Create("tenantless", isShared: false), default);

        result.Succeeded.Should().BeTrue(result.ErrorMessage);
        (await StoredAsync("tenantless"))!.TenantId.Should().BeNull();
    }

    [Test]
    public async Task TheEditPathIgnoresTheSharedFlag()
    {
        // Moving an existing row between partitions changes who sees it and which rows the unique
        // index constrains it against. The DTO round-trips through the browser, so a client setting
        // IsShared on an edit must do nothing at all.
        await AddEdit(Holder, TenantA).Handle(Create("stays-private", isShared: false), default);
        var created = (await StoredAsync("stays-private"))!;

        var edit = Create("stays-private", isShared: true);
        edit.Id = created.Id;
        edit.Text = "renamed";
        var result = await AddEdit(Holder, TenantA).Handle(edit, default);

        result.Succeeded.Should().BeTrue(result.ErrorMessage);
        var after = (await StoredAsync("stays-private"))!;
        after.Text.Should().Be("renamed", "the edit itself must still work");
        after.TenantId.Should().Be(TenantA, "the flag must not have moved the row");
    }

    // ---- containment: this must not become a general escape from stamping -----------------------

    [Test]
    public void NothingElseInTheDomainCanOptOutOfStamping()
    {
        // Opt-in by TYPE is the primary containment, so the list of types that opted in is asserted
        // rather than described. Adding one is then a deliberate act with a failing test attached -
        // which is exactly what Pass 32 A1 said a comment could never be.
        var optedIn = typeof(PicklistSet).Assembly.GetTypes()
            .Where(t => typeof(IMayBeShared).IsAssignableFrom(t) && t is { IsInterface: false, IsAbstract: false })
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        optedIn.Should().Equal(nameof(PicklistSet));
    }

    [Test]
    public void IMustHaveTenantEntitiesAreStructurallyOutOfReach()
    {
        // The interceptor's IMustHaveTenant branch is untouched and stays unconditional. "May have
        // no tenant" and "must have one" are different contracts and only the first has a shared
        // partition to opt into, so no IMustHaveTenant entity can implement the marker.
        typeof(PicklistSet).Assembly.GetTypes()
            .Where(t => typeof(IMayBeShared).IsAssignableFrom(t) && !t.IsInterface)
            .Should().NotContain(t => typeof(IMustHaveTenant).IsAssignableFrom(t));
    }

    [Test]
    public async Task TheFlagNeverReachesTheDatabase()
    {
        // [NotMapped] is load-bearing: if the flag were persisted, "is this row shared" would have
        // two stored answers. A row read back must never carry a true flag, whatever created it.
        await AddEdit(Holder, TenantA).Handle(Create("installation-wide", isShared: true), default);

        (await StoredAsync("installation-wide"))!.CreateAsShared.Should().BeFalse();
    }

    [Test]
    public async Task AnUnflaggedRowIsStillStampedByTheInterceptor()
    {
        // Pass 24's rule, unchanged, asserted at the interceptor rather than through the handler:
        // an IMayHaveTenant row saved with no flag and no tenant still gets the ambient one.
        await using var db = Factory.Build(_connection, new Ambient(Holder, TenantA));
        db.PicklistSets.Add(new PicklistSet
        {
            Name = Picklist.Unit, Value = "unflagged", Text = "unflagged"
        });
        await db.SaveChangesAsync();

        (await StoredAsync("unflagged"))!.TenantId.Should().Be(TenantA);
    }

    [Test]
    public async Task AFlaggedRowSavedDIRECTLYIsNotStamped()
    {
        // The mechanism itself, in isolation from the handler - so a future change that stops the
        // handler setting the flag fails HERE as well as above, and the two failures say different
        // things: this one that the interceptor stopped honouring it, that one that the handler
        // stopped setting it.
        await using var db = Factory.Build(_connection, new Ambient(Holder, TenantA));
        db.PicklistSets.Add(new PicklistSet
        {
            Name = Picklist.Unit, Value = "flagged-direct", Text = "flagged-direct", CreateAsShared = true
        });
        await db.SaveChangesAsync();

        (await StoredAsync("flagged-direct"))!.TenantId.Should().BeNull();
    }

    [Test]
    public async Task TheFlagGrantsNothing_ARefusedCallerNeverReachesTheInterceptor()
    {
        // Setting the flag is not a way around the right: the handler refuses first, so the entity
        // is never added and the interceptor is never asked. Asserted by there being no row at all
        // rather than a private one - a post-insert correction would have left one.
        var result = await AddEdit(NonHolder, TenantA)
            .Handle(Create("never-written", isShared: true), default);

        result.Succeeded.Should().BeFalse();
        await using var db = Factory.Build(_connection, NoPrincipal);
        (await db.PicklistSets.IgnoreQueryFilters().CountAsync(p => p.Value == "never-written"))
            .Should().Be(0, "filters off, so a PRIVATE row written by mistake would be counted too");
    }
}
