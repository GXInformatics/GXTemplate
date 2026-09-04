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
using CleanArchitecture.Blazor.Application.Features.PicklistSets.Commands.Delete;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Interceptors;
using Moq;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Features.PicklistSets;

/// <summary>
/// Who may write the shared picklist values every tenant sees.
/// </summary>
/// <remarks>
/// <para>
/// <b>Driven through the real command handlers, not through the rule.</b> Both commands go through
/// Mediator and are reachable by any caller, so a guard proven only at
/// <c>SharedPicklistWrite.IsAllowedAsync</c> would prove the rule and not its enforcement - and the
/// grid was the thing that used to decide. Every assertion below sends a command and reads the
/// <c>Result</c>, then checks the database to see whether the write actually happened.
/// </para>
/// <para>
/// <b>Narrowed, not emptied.</b> A guard that refused every write would satisfy every negative
/// assertion here, so each refusal is paired with the same operation on the principal's OWN tenant
/// row succeeding. That control matters more than usual: the failure mode of a permission guard is
/// almost always over-refusal, and the single-tenant deployment is the one it would break first.
/// </para>
/// <para>
/// <b>This is a WRITE right and it grants no sight of anything.</b> Pass 31 §C declined a
/// cross-tenant READ escape and that stands - <c>AHolderStillCannotReachAnotherTenantsPrivateRow</c>
/// asserts the holder gains nothing over another tenant's private rows, because "manage shared" and
/// "see everything" are the two capabilities most easily conflated.
/// </para>
/// </remarks>
[TestFixture]
public class SharedPicklistWriteTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private const string Holder = "user-holder";
    private const string NonHolder = "user-non-holder";

    private const int SharedRow = 1;
    private const int TenantARow = 2;
    private const int TenantBRow = 3;

    private SqliteConnection _connection = null!;

    /// <summary>An ambient principal in one tenant, or none at all.</summary>
    private sealed class Ambient : IUserContextAccessor
    {
        public Ambient(string? userId, string? tenantId) =>
            Current = userId is null ? null : new UserContext(userId, userId, TenantId: tenantId);
        public UserContext? Current { get; private set; }
        public IDisposable Push(UserContext context) => throw new NotSupportedException();
        public void Clear() => Current = null;
    }

    /// <summary>
    /// Answers the permission query for one named holder and nobody else.
    /// </summary>
    /// <remarks>
    /// Returns an UNASSIGNED row for the non-holder rather than an empty list, because
    /// <c>PermissionModel.Assigned</c> is the field the rule reads and a list that simply omitted the
    /// permission would pass even against a guard that forgot to check <c>Assigned</c> at all.
    /// </remarks>
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

    /// <summary>
    /// Builds contexts the way the application does: filtered by the ambient principal AND carrying
    /// the real stamping interceptor.
    /// </summary>
    /// <remarks>
    /// <b>The interceptor is not optional here, and leaving it out hid a real gap.</b> The create
    /// guard reasons about the tenant a new row WILL be stamped with, and the stamping is
    /// <c>AuditableEntityInterceptor</c>'s, registered on the context options rather than written by
    /// the handler. Without it every created row came back with a null tenant, which made the
    /// tenant-scoped create look like it produced a SHARED row - the exact thing the guard exists to
    /// prevent, appearing to have happened.
    /// <para>
    /// So the interceptor is wired exactly as <c>TenantStampingTests</c> wires it. That makes
    /// <c>ANonHolderStillCreatesARowInTheirOwnTenant</c> an end-to-end assertion that the guard's
    /// prediction and the interceptor's behaviour agree - see the pass report on why those are two
    /// copies of one rule.
    /// </para>
    /// </remarks>
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
            dateTime.SetupGet(x => x.UtcNow).Returns(new DateTime(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc));

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

        // Seeded with no ambient principal, the only way to write a shared row.
        await using var db = Factory.Build(_connection, NoPrincipal);
        await db.Database.EnsureCreatedAsync();

        // The interceptor writes an AuditTrail row for every save, and AuditTrail.UserId is a real
        // foreign key to AspNetUsers. Without these rows every SUCCESSFUL write in this fixture fails
        // on the constraint - which would have made the refusals look like the only thing that works.
        db.Users.AddRange(
            new ApplicationUser { Id = Holder, UserName = Holder, Email = $"{Holder}@example.test" },
            new ApplicationUser { Id = NonHolder, UserName = NonHolder, Email = $"{NonHolder}@example.test" });

        db.PicklistSets.AddRange(
            Row(SharedRow, "shipped", null),
            Row(TenantARow, "a-only", TenantA),
            Row(TenantBRow, "b-only", TenantB));
        await db.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown() => await _connection.DisposeAsync();

    private static PicklistSet Row(int id, string value, string? tenantId) => new()
    {
        Id = id,
        Name = Picklist.Brand,
        Value = value,
        Text = value,
        TenantId = tenantId
    };

    private AddEditPicklistSetCommandHandler AddEdit(string? userId, string? tenantId)
    {
        var accessor = new Ambient(userId, tenantId);
        return new AddEditPicklistSetCommandHandler(
            new Factory(_connection, accessor),
            new MapsterObjectMapper(MapsterConfiguration.Create()),
            new Permissions(),
            accessor);
    }

    private DeletePicklistSetCommandHandler Delete(string? userId, string? tenantId)
    {
        var accessor = new Ambient(userId, tenantId);
        return new DeletePicklistSetCommandHandler(new Factory(_connection, accessor), new Permissions(), accessor);
    }

    /// <summary>Reads a row past the tenant filter, so a test can see what actually happened.</summary>
    private async Task<PicklistSet?> StoredAsync(int id)
    {
        await using var db = Factory.Build(_connection, NoPrincipal);
        return await db.PicklistSets.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Id == id);
    }

    private static AddEditPicklistSetCommand Edit(int id, string value) =>
        new() { Id = id, Name = Picklist.Brand, Value = value, Text = value };

    // ---- the refusals ---------------------------------------------------------------------------

    [Test]
    public async Task ANonHolderCannotEditASharedRow()
    {
        // RED before Pass 32: the edit succeeded, and it changed a value every tenant sees.
        var result = await AddEdit(NonHolder, TenantA)
            .Handle(Edit(SharedRow, "hijacked"), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Be(SharedPicklistWrite.Refused);

        (await StoredAsync(SharedRow))!.Value.Should().Be("shipped",
            "the refusal must be a refusal, not a message beside a write that happened anyway");
    }

    [Test]
    public async Task ANonHolderCannotDeleteASharedRow()
    {
        var result = await Delete(NonHolder, TenantA)
            .Handle(new DeletePicklistSetCommand([SharedRow]), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Be(SharedPicklistWrite.Refused);
        (await StoredAsync(SharedRow)).Should().NotBeNull();
    }

    [Test]
    public async Task AMixedDeleteIsRefusedWholesaleRatherThanPartiallyApplied()
    {
        // One shared row in a multi-row selection refuses the whole command. A half-applied delete
        // would leave the caller to work out which rows survived, and a retry would then be a
        // different request from the one they issued.
        var result = await Delete(NonHolder, TenantA)
            .Handle(new DeletePicklistSetCommand([SharedRow, TenantARow]), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        (await StoredAsync(SharedRow)).Should().NotBeNull();
        (await StoredAsync(TenantARow)).Should().NotBeNull(
            "the caller's own row must survive a refusal it was only swept into");
    }

    [Test]
    public async Task ATenantlessNonHolderCannotCREATEASharedRow()
    {
        // The case a guard on edit and delete alone would miss. A principal with no tenant is stamped
        // with nothing, so the row it creates is installation-wide - the same capability, reached
        // without ever touching an existing shared row.
        var result = await AddEdit(NonHolder, tenantId: null)
            .Handle(new AddEditPicklistSetCommand { Name = Picklist.Brand, Value = "smuggled", Text = "smuggled" },
                CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Be(SharedPicklistWrite.Refused);

        await using var db = Factory.Build(_connection, NoPrincipal);
        (await db.PicklistSets.IgnoreQueryFilters().AnyAsync(p => p.Value == "smuggled")).Should().BeFalse();
    }

    // ---- narrowed, not emptied ------------------------------------------------------------------

    [Test]
    public async Task ANonHolderStillEditsTheirOwnTenantsRow()
    {
        // THE control. A guard that refused everything passes every assertion above, and the
        // deployment it would break first is the single-tenant one.
        var result = await AddEdit(NonHolder, TenantA)
            .Handle(Edit(TenantARow, "renamed"), CancellationToken.None);

        result.Succeeded.Should().BeTrue(result.ErrorMessage);
        (await StoredAsync(TenantARow))!.Value.Should().Be("renamed");
    }

    [Test]
    public async Task ANonHolderStillDeletesTheirOwnTenantsRow()
    {
        var result = await Delete(NonHolder, TenantA)
            .Handle(new DeletePicklistSetCommand([TenantARow]), CancellationToken.None);

        result.Succeeded.Should().BeTrue(result.ErrorMessage);
        (await StoredAsync(TenantARow)).Should().BeNull();
    }

    [Test]
    public async Task ANonHolderStillCreatesARowInTheirOwnTenant()
    {
        var result = await AddEdit(NonHolder, TenantA)
            .Handle(new AddEditPicklistSetCommand { Name = Picklist.Brand, Value = "mine", Text = "mine" },
                CancellationToken.None);

        result.Succeeded.Should().BeTrue(result.ErrorMessage);

        var created = await StoredAsync(result.Data);
        created!.TenantId.Should().Be(TenantA,
            "a tenant-scoped principal creates PRIVATE rows - that is what makes the create path safe " +
            "for them without the right");
    }

    // ---- the holder -----------------------------------------------------------------------------

    [Test]
    public async Task AHolderEditsASharedRow()
    {
        var result = await AddEdit(Holder, TenantA)
            .Handle(Edit(SharedRow, "corrected"), CancellationToken.None);

        result.Succeeded.Should().BeTrue(result.ErrorMessage);
        (await StoredAsync(SharedRow))!.Value.Should().Be("corrected");
    }

    [Test]
    public async Task AHolderDeletesASharedRow()
    {
        var result = await Delete(Holder, TenantA)
            .Handle(new DeletePicklistSetCommand([SharedRow]), CancellationToken.None);

        result.Succeeded.Should().BeTrue(result.ErrorMessage);
        (await StoredAsync(SharedRow)).Should().BeNull();
    }

    [Test]
    public async Task AHolderStillCannotReachAnotherTenantsPrivateRow()
    {
        // ManageShared is a write right over the SHARED partition. It is not the cross-tenant read
        // escape Pass 31 §C declined, and the two are the easiest pair in this design to conflate.
        // The query filter is what stops this, and the holder cannot drop it.
        var result = await AddEdit(Holder, TenantA)
            .Handle(Edit(TenantBRow, "reached"), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not found",
            "another tenant's row is invisible, so it is a not-found rather than a permission refusal");

        (await StoredAsync(TenantBRow))!.Value.Should().Be("b-only");
    }

    // ---- the rule itself ------------------------------------------------------------------------

    [Test]
    public void ARowWithNoTenantIsShared_AndOneWithATenantIsNot()
    {
        SharedPicklistWrite.IsShared(null).Should().BeTrue();
        SharedPicklistWrite.IsShared("").Should().BeTrue(
            "an empty tenant id is neither a real tenant nor a private row; treating it as private " +
            "would leave it editable by everyone and visible to nobody");
        SharedPicklistWrite.IsShared(TenantA).Should().BeFalse();
    }

    [Test]
    public async Task TheRuleFailsClosedWithNoPrincipal()
    {
        // Every path that is not an affirmative grant must refuse: no user id, an unknown user, an
        // empty permission list.
        (await SharedPicklistWrite.MayManageSharedAsync(new Permissions(), userId: null))
            .Should().BeFalse();
        (await SharedPicklistWrite.MayManageSharedAsync(new Permissions(), userId: ""))
            .Should().BeFalse();
        (await SharedPicklistWrite.MayManageSharedAsync(new Permissions(), "nobody-in-particular"))
            .Should().BeFalse();
    }

    [Test]
    public async Task TheRuleSkipsThePermissionQueryWhenNoSharedRowIsInvolved()
    {
        // A short-circuit on the cheap side: skipping means allowing, and it is reached only when
        // nothing shared is being touched. Asserted with a service that THROWS, so a future edit
        // that queried unconditionally would fail here rather than merely cost a round trip.
        var explodes = new ThrowingPermissions();

        (await SharedPicklistWrite.IsAllowedAsync([TenantA, TenantB], explodes, NonHolder))
            .Should().BeTrue();
    }

    private sealed class ThrowingPermissions : IPermissionQueryService
    {
        public Task<IList<PermissionModel>> GetAllPermissionsByUserId(string userId) =>
            throw new InvalidOperationException(
                "The permission query must not run when no shared row is affected.");

        public Task<IList<PermissionModel>> GetAllPermissionsByRoleId(string roleId) =>
            throw new InvalidOperationException("Not used.");
    }
}
