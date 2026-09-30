#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Application.Features.SecuritySettings;
using CleanArchitecture.Blazor.Application.Features.SecuritySettings.Commands;
using CleanArchitecture.Blazor.Application.Features.SecuritySettings.Queries;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Npgsql;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;

namespace CleanArchitecture.Blazor.Application.UnitTests.Features.SecuritySettings;

/// <summary>
/// <c>Permissions.SecuritySettings.ManageInstallationPolicy</c>: who may write the idle policy, which
/// is one row in force for every tenant at once.
/// </summary>
/// <remarks>
/// <para>
/// <b>Through the handler, not the rule.</b> Every assertion below sends a real
/// <c>UpdateSecurityPolicyCommand</c> and then reads the row back, because the screen was the thing
/// that used to decide and a guard proved only at <c>InstallationPolicyWrite.IsAllowedAsync</c>
/// would prove the rule and not its enforcement. The command goes through Mediator and is reachable
/// by any caller whatever the page renders.
/// </para>
/// <para>
/// <b>Narrowed, not emptied.</b> <see cref="ANonHolderCanStillREADThePolicy"/> is the control that
/// matters most: the failure mode of a permission guard is over-refusal, and a guard that also
/// blocked <c>SecuritySettings.View</c> would satisfy every negative assertion here while removing
/// the screen's purpose - an administrator needs to see the window to answer "why was I signed
/// out?", whoever may change it.
/// </para>
/// <para>
/// <b>Pass 32 A5's trap is taken seriously.</b> <c>SecurityPolicy</c> is <c>IAuditable</c>, so the
/// real interceptor writes an <c>AuditTrail</c> row with a foreign key to <c>AspNetUsers</c>. The
/// fixture registers the interceptor and creates real user rows, so a successful save exercises the
/// same path production does; without them every refusal would pass and every success would fail on
/// the constraint, and the fixture would be green while proving nothing works.
/// </para>
/// </remarks>
[TestFixture]
public class InstallationPolicyWriteTests
{
    private const string Holder = "user-holder";
    private const string NonHolder = "user-non-holder";

    private NpgsqlConnection _connection = null!;

    private sealed class Ambient : IUserContextAccessor
    {
        public Ambient(string? userId) =>
            Current = userId is null ? null : new UserContext(userId, userId, TenantId: "tenant-a");
        public UserContext? Current { get; private set; }
        public IDisposable Push(UserContext context) => throw new NotSupportedException();
        public void Clear() => Current = null;
    }

    /// <summary>
    /// Answers for one named holder and nobody else, returning an UNASSIGNED row for the non-holder
    /// rather than an empty list - <c>Assigned</c> is the field the rule reads, and a list that
    /// simply omitted the permission would pass even against a guard that never checked it.
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
                    ClaimValue = Application.Common.Security.Permissions.SecuritySettings.ManageInstallationPolicy,
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
        private readonly NpgsqlConnection _connection;
        private readonly IUserContextAccessor _accessor;

        public Factory(NpgsqlConnection connection, IUserContextAccessor accessor)
        {
            _connection = connection;
            _accessor = accessor;
        }

        public ValueTask<IApplicationDbContext> CreateAsync(CancellationToken ct = default) =>
            new(Build(_connection, _accessor));

        public static ApplicationDbContext Build(NpgsqlConnection connection, IUserContextAccessor accessor)
        {
            var dateTime = new Mock<IDateTime>();
            dateTime.SetupGet(x => x.UtcNow).Returns(new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc));

            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(connection)
                .AddInterceptors(new AuditableEntityInterceptor(accessor, dateTime.Object))
                .Options;

            return new ApplicationDbContext(options, accessor);
        }
    }

    /// <summary>Counts cache invalidations, so "a refused save invalidates nothing" is observable.</summary>
    private sealed class CountingProvider : IIdleTimeoutPolicyProvider
    {
        public int Invalidations { get; private set; }
        public bool Enabled => true;

        public Task<AdministeredIdleTimeoutPolicy> GetAdministeredAsync(CancellationToken ct = default) =>
            Task.FromResult(new AdministeredIdleTimeoutPolicy(15, 60));

        public Task<IdleTimeoutPolicy> GetEffectiveAsync(
            System.Security.Claims.ClaimsPrincipal user, CancellationToken ct = default) =>
            Task.FromResult(new IdleTimeoutPolicy(true, 15, 60));

        public void Invalidate() => Invalidations++;
        public void InvalidateUser(string userId) { }
    }

    private CountingProvider _provider = null!;

    private static readonly IUserContextAccessor NoPrincipal = new Ambient(null);

    [SetUp]
    public async Task SetUp()
    {
        _connection = UnitTestDatabase.NewConnection();
        await _connection.OpenAsync();
        _provider = new CountingProvider();

        await using var db = Factory.Build(_connection, NoPrincipal);
        await UnitTestDatabase.ResetAsync();

        db.Users.AddRange(
            new ApplicationUser { Id = Holder, UserName = Holder, Email = $"{Holder}@example.test" },
            new ApplicationUser { Id = NonHolder, UserName = NonHolder, Email = $"{NonHolder}@example.test" });
        await db.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown() => await _connection.DisposeAsync();

    private UpdateSecurityPolicyCommandHandler Update(string? userId) =>
        new(new Factory(_connection, new Ambient(userId)), _provider, new Permissions(),
            new Ambient(userId));

    private async Task SeedPolicyAsync(int idleMinutes, int countdownSeconds)
    {
        await using var db = Factory.Build(_connection, NoPrincipal);
        db.SecurityPolicies.Add(new SecurityPolicy
        {
            IdleTimeoutMinutes = idleMinutes,
            CountdownSeconds = countdownSeconds
        });
        await db.SaveChangesAsync();
    }

    private async Task<SecurityPolicy?> StoredAsync()
    {
        await using var db = Factory.Build(_connection, NoPrincipal);
        return await db.SecurityPolicies.AsNoTracking().OrderBy(p => p.Id).FirstOrDefaultAsync();
    }

    private static UpdateSecurityPolicyCommand Save(int idle, int countdown) =>
        new() { IdleTimeoutMinutes = idle, CountdownSeconds = countdown };

    // ---- the guard, through the handler --------------------------------------------------------

    [Test]
    public async Task ANonHolderCannotSaveThePolicy()
    {
        await SeedPolicyAsync(15, 60);

        var result = await Update(NonHolder).Handle(Save(120, 600), default);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Be(InstallationPolicyWrite.Refused);

        var stored = (await StoredAsync())!;
        stored.IdleTimeoutMinutes.Should().Be(15, "the stored row must be re-read - a refusal beside " +
                                                  "a write that happened anyway is not a refusal");
        stored.CountdownSeconds.Should().Be(60);
    }

    [Test]
    public async Task AHolderCanSaveThePolicy()
    {
        await SeedPolicyAsync(15, 60);

        var result = await Update(Holder).Handle(Save(45, 90), default);

        result.Succeeded.Should().BeTrue(result.ErrorMessage);

        var stored = (await StoredAsync())!;
        stored.IdleTimeoutMinutes.Should().Be(45);
        stored.CountdownSeconds.Should().Be(90);
    }

    [Test]
    public async Task ARefusedSaveDoesNotSeedARowOnAFreshDatabase()
    {
        // The guard runs BEFORE the row is read or created. The handler's fresh-database branch
        // creates the row when none exists, so a guard placed after it would refuse the save and
        // still leave a policy row behind - a write, performed by a refusal.
        (await StoredAsync()).Should().BeNull("nothing has seeded a policy yet");

        var result = await Update(NonHolder).Handle(Save(30, 30), default);

        result.Succeeded.Should().BeFalse();
        (await StoredAsync()).Should().BeNull("a refused save must write nothing at all");
    }

    [Test]
    public async Task ARefusedSaveDoesNotInvalidateTheCache()
    {
        await SeedPolicyAsync(15, 60);

        await Update(NonHolder).Handle(Save(45, 90), default);

        _provider.Invalidations.Should().Be(0,
            "nothing changed, so dropping the cached policy would be pure cost on a value read on " +
            "every authenticated request");
    }

    [Test]
    public async Task AnAcceptedSaveDoesInvalidateTheCache()
    {
        await SeedPolicyAsync(15, 60);

        await Update(Holder).Handle(Save(45, 90), default);

        _provider.Invalidations.Should().Be(1);
    }

    // ---- narrowed, not emptied ------------------------------------------------------------------

    [Test]
    public async Task ANonHolderCanStillREADThePolicy()
    {
        // THE control for this pass. Reading is gated by SecuritySettings.View and is deliberately
        // untouched: the values are what an administrator needs to answer "why was I signed out?",
        // whoever may change them.
        await SeedPolicyAsync(15, 60);

        var settings = new Mock<IIdleTimeoutSettings>();
        settings.SetupGet(x => x.Enabled).Returns(true);
        settings.SetupGet(x => x.MinIdleTimeoutMinutes).Returns(1);
        settings.SetupGet(x => x.MaxIdleTimeoutMinutes).Returns(120);
        settings.SetupGet(x => x.AllowUserOverride).Returns(true);

        var read = await new GetSecurityPolicyQueryHandler(_provider, settings.Object)
            .Handle(new GetSecurityPolicyQuery(), default);

        read.Succeeded.Should().BeTrue(read.ErrorMessage);
        read.Data!.IdleTimeoutMinutes.Should().Be(15);
        read.Data.MaxIdleTimeoutMinutes.Should().Be(120,
            "the bounds travel with the policy so the screen can state them");
    }

    // ---- the rule -------------------------------------------------------------------------------

    [Test]
    public async Task TheRuleFailsClosedWithNoPrincipal()
    {
        (await InstallationPolicyWrite.IsAllowedAsync(new Permissions(), null)).Should().BeFalse();
        (await InstallationPolicyWrite.IsAllowedAsync(new Permissions(), string.Empty)).Should().BeFalse();
    }

    [Test]
    public async Task ANonHolderIsRefusedEvenWithAPrincipal()
    {
        (await InstallationPolicyWrite.IsAllowedAsync(new Permissions(), NonHolder)).Should().BeFalse();
        (await InstallationPolicyWrite.IsAllowedAsync(new Permissions(), Holder)).Should().BeTrue();
    }

    [Test]
    public void TheRefusalNamesTheScopeRatherThanTheScreen()
    {
        // The surprising part is not that a save was refused but that the value reaches every
        // tenant; the message has to carry that or the reader learns nothing from it.
        InstallationPolicyWrite.Refused.Should().Contain("every tenant");
    }

    // ---- the registry and the wiring ------------------------------------------------------------

    [Test]
    public void TheAdministratorHoldsTheRightByDefault()
    {
        // The single-tenant case, which has nearly gone wrong four times. EnsureAdministratorAsync
        // assigns the bootstrap administrator Tenants.First(), so the sole administrator IS
        // tenant-scoped; an ungranted default would leave a single-tenant installation permanently
        // unable to change its own idle timeout.
        AdministratorPermissionRegistry.Granted.Should().Contain(
            Application.Common.Security.Permissions.SecuritySettings.ManageInstallationPolicy);
        AdministratorPermissionRegistry.Excluded.Keys.Should().NotContain(
            Application.Common.Security.Permissions.SecuritySettings.ManageInstallationPolicy);
    }

    [Test]
    public void TheAccessRightsPropertyIsSpelledLikeTheConstant()
    {
        // PermissionService builds the claim string from the PROPERTY NAME, so a mismatch here is a
        // right the screen checks under a name nothing ever grants - see LogsAccessRights.
        typeof(SecuritySettingsAccessRights)
            .GetProperty(nameof(SecuritySettingsAccessRights.ManageInstallationPolicy))
            .Should().NotBeNull();

        Application.Common.Security.Permissions.SecuritySettings.ManageInstallationPolicy
            .Should().EndWith("." + nameof(SecuritySettingsAccessRights.ManageInstallationPolicy));
    }

    [Test]
    public void TheDescriptionStatesThatItSpansEveryTenant()
    {
        // This string is what the role editor shows an administrator deciding whether to grant it -
        // the Pass 35 lesson. Asserted for MEANING rather than an exact sentence.
        var description = typeof(Application.Common.Security.Permissions.SecuritySettings)
            .GetField(nameof(Application.Common.Security.Permissions.SecuritySettings.ManageInstallationPolicy))!
            .GetCustomAttributes(typeof(DescriptionAttribute), false)
            .Cast<DescriptionAttribute>()
            .FirstOrDefault()?.Description ?? string.Empty;

        description.Should().NotBeEmpty();
        (description.Contains("every tenant", StringComparison.OrdinalIgnoreCase)
         || description.Contains("installation", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue($"it currently reads: \"{description}\"");
    }
}
