#nullable enable
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Services.Identity;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Server.UI.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Endpoints;

/// <summary>
/// The per-object half of the /files endpoint's authorization.
///
/// Authentication alone would already be an improvement on what it replaces - the /Files static
/// mount served every stored object to anyone, because static-file middleware runs before
/// authorization. But document file names come from whoever uploaded them ("invoice.png"), so they
/// are guessable, and a private document belongs to one user in one tenant. Document keys therefore
/// carry the Documents.Download permission plus the same visibility rule the download button uses.
/// Profile pictures deliberately do not: seven render sites show other users' avatars.
/// </summary>
[TestFixture]
public class FileEndpointsAuthorizationTests
{
    private const string TenantId = "tenant-1";
    private const string OtherTenantId = "tenant-2";
    private const string UserA = "user-a";
    private const string UserB = "user-b";

    private SqliteConnection _connection = null!;
    private ApplicationDbContext _db = null!;
    private IApplicationDbContextFactory _factory = null!;
    private IAuthorizationService _permitAll = null!;
    private IAuthorizationService _denyAll = null!;

    private const string PrivateKeyOfA = "Documents/private-of-a.png";
    private const string PublicKeyOfA = "Documents/public-of-a.png";
    private const string KeyInOtherTenant = "Documents/other-tenant.png";

    [SetUp]
    public async Task SetUp()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new ApplicationDbContext(options);
        await _db.Database.EnsureCreatedAsync();

        _db.Tenants.Add(new Tenant { Id = TenantId, Name = "One" });
        _db.Tenants.Add(new Tenant { Id = OtherTenantId, Name = "Two" });
        _db.Users.Add(new ApplicationUser { Id = UserA, UserName = "a", Email = "a@example.com", TenantId = TenantId });
        _db.Users.Add(new ApplicationUser { Id = UserB, UserName = "b", Email = "b@example.com", TenantId = TenantId });
        await _db.SaveChangesAsync();

        Add(PrivateKeyOfA, isPublic: false, owner: UserA, tenant: TenantId);
        Add(PublicKeyOfA, isPublic: true, owner: UserA, tenant: TenantId);
        Add(KeyInOtherTenant, isPublic: true, owner: UserB, tenant: OtherTenantId);
        await _db.SaveChangesAsync();

        var factory = new Mock<IApplicationDbContextFactory>();
        factory.Setup(x => x.CreateAsync(It.IsAny<CancellationToken>()))
            .Returns(() => new ValueTask<IApplicationDbContext>(
                new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options)));
        _factory = factory.Object;

        _permitAll = BuildAuthorizationService(grantDownload: true);
        _denyAll = BuildAuthorizationService(grantDownload: false);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private void Add(string storageKey, bool isPublic, string owner, string tenant) =>
        _db.Documents.Add(new Document
        {
            Title = storageKey,
            IsPublic = isPublic,
            CreatedById = owner,
            TenantId = tenant,
            StorageKey = storageKey,
            PublicUrl = "/files/" + storageKey
        });

    private static IAuthorizationService BuildAuthorizationService(bool grantDownload)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationCore(options =>
            options.AddPolicy(Permissions.Documents.Download, policy =>
            {
                if (grantDownload) policy.RequireAssertion(_ => true);
                else policy.RequireAssertion(_ => false);
            }));
        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    /// <summary>
    /// A principal in the shape the application actually produces: a name identifier and <b>no
    /// tenant claim</b>.
    /// </summary>
    /// <remarks>
    /// <b>This helper used to add a <c>TenantId</c> claim, and that is why these tests passed while
    /// the endpoint was broken.</b> Pass 36 measured <c>AspNetUserClaims</c> as empty in a freshly
    /// seeded installation: the only writer of that claim is
    /// <c>TenantSwitchService.RefreshUserClaimsAsync</c>, reachable only from a tenant switch, so a
    /// user who has never switched carries none. The fixture was manufacturing a claim no real
    /// principal had, and <c>ADocumentInAnotherTenant_IsRefused_EvenThoughItIsPublic</c> was green
    /// against a population of one - users who had switched tenant at least once.
    /// <para>
    /// The tenant now comes from <see cref="Loader"/>, which is where the endpoint reads it from
    /// since Pass 38, so the fixture and production resolve it the same way.
    /// </para>
    /// </remarks>
    private static ClaimsPrincipal Principal(string userId) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId)
        }, authenticationType: "Test"));

    /// <summary>
    /// A principal that DOES carry a tenant claim - the population for whom the old code was
    /// correct. Used only by the regression control, to prove the fix did not break them.
    /// </summary>
    private static ClaimsPrincipal PrincipalWithTenantClaim(string userId, string tenantId) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(ApplicationClaimTypes.TenantId, tenantId)
        }, authenticationType: "Test"));

    /// <summary>
    /// Resolves the tenant from a user row, as <c>UserContextLoader</c> does, and returns null for a
    /// principal it cannot resolve - the fail-closed case.
    /// </summary>
    private sealed class Loader : IUserContextLoader
    {
        private readonly Dictionary<string, string?> _tenantByUserId;

        public Loader(Dictionary<string, string?> tenantByUserId) => _tenantByUserId = tenantByUserId;

        public Task<UserContext?> LoadAsync(ClaimsPrincipal principal, CancellationToken ct = default)
        {
            var userId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (userId is null || !_tenantByUserId.TryGetValue(userId, out var tenantId))
            {
                return Task.FromResult<UserContext?>(null);
            }

            return Task.FromResult<UserContext?>(
                new UserContext(UserId: userId, UserName: userId, TenantId: tenantId));
        }

        public void ClearUserContextCache(string userId) { }
    }

    /// <summary>The two seeded users, each in their real tenant.</summary>
    private static Loader RealTenants() => new(new Dictionary<string, string?>
    {
        [UserA] = TenantId,
        [UserB] = TenantId
    });

    private Task<bool> IsPermitted(
        string key, ClaimsPrincipal user, IAuthorizationService authorization, IUserContextLoader? loader = null) =>
        FileEndpoints.IsPermittedAsync(
            key, user, _factory, authorization, loader ?? RealTenants(), CancellationToken.None);

    [Test]
    public async Task AProfilePictureKey_NeedsOnlyAuthentication()
    {
        // Any authenticated user may fetch any avatar - the alternative breaks every grid and
        // presence list that shows somebody else's picture, and protects nothing that is not
        // already displayed next to that person's name.
        (await IsPermitted($"ProfilePictures/{UserA}/avatar.jpg", Principal(UserB), _denyAll))
            .Should().BeTrue();
    }

    [Test]
    public async Task TheOwner_MayFetchTheirOwnPrivateDocument()
    {
        (await IsPermitted(PrivateKeyOfA, Principal(UserA), _permitAll)).Should().BeTrue();
    }

    [Test]
    public async Task AnotherUserInTheSameTenant_MayNotFetchAPrivateDocument()
    {
        // Authentication alone would have served this. The file name is the uploader's own
        // ("invoice.png"), so guessing it is not a stretch.
        (await IsPermitted(PrivateKeyOfA, Principal(UserB), _permitAll)).Should().BeFalse();
    }

    [Test]
    public async Task APublicDocument_IsReadableAcrossUsersInsideTheTenant()
    {
        (await IsPermitted(PublicKeyOfA, Principal(UserB), _permitAll)).Should().BeTrue();
    }

    [Test]
    public async Task ADocumentInAnotherTenant_IsRefused_EvenThoughItIsPublic()
    {
        (await IsPermitted(KeyInOtherTenant, Principal(UserA), _permitAll)).Should().BeFalse();
    }

    [Test]
    public async Task WithoutTheDownloadPermission_NoDocumentKeyIsServed()
    {
        (await IsPermitted(PublicKeyOfA, Principal(UserA), _denyAll)).Should().BeFalse();
    }

    [Test]
    public async Task ADocumentKeyWithNoMatchingRow_IsRefused()
    {
        (await IsPermitted("Documents/never-existed.png", Principal(UserA), _permitAll))
            .Should().BeFalse();
    }

    [TestCase("documents/private-of-a.png")]
    [TestCase("Documents\\private-of-a.png")]
    public async Task TheDocumentPrefixIsMatchedRegardlessOfCasingOrSeparator(string key)
    {
        // The prefix test is what decides whether the per-object check runs at all, so a key that
        // differs only in casing or slash direction must not slip past it.
        (await IsPermitted(key, Principal(UserB), _permitAll)).Should().BeFalse();
    }

    // ---- Pass 44: deny by default -----------------------------------------------------------------

    [TestCase("Reports/x.pdf")]
    [TestCase("Images/x.png")]
    [TestCase("x.pdf")]
    [TestCase(" Documents/private-of-a.png")]
    [TestCase("Documents /private-of-a.png")]
    public async Task AKeyUnderAnUnlistedPrefix_IsRefused_EvenWithEveryPermission(string key)
    {
        // Before Pass 44 every one of these returned TRUE: any first segment other than exactly
        // "Documents" was served to any authenticated caller. The last two are the sharp end of
        // that - the storage layer trims segments, so " Documents/..." reads the very bytes the
        // Documents rule exists to protect, having skipped that rule. "Images" is a real UploadType
        // with no visibility rule, which is exactly the case deny-by-default is for.
        (await IsPermitted(key, Principal(UserB), _permitAll)).Should().BeFalse();
    }

    // ---- Pass 38: the tenant comes from the user row, not the claim -----------------------------

    [Test]
    public async Task AUserWhoHasNeverSwitchedTenant_IsStillConfinedToTheirTenant()
    {
        // THE regression this pass exists for, and the one the old fixture could not see.
        //
        // Before Pass 38 this returned TRUE: the principal carries no TenantId claim (the production
        // shape - the claim's only writer is a tenant switch), the endpoint read the claim, got
        // nothing, and passed string.Empty into VisibleDocumentSpecification, whose no-tenant branch
        // drops the tenant clause. Every PUBLIC document in the installation was served - and
        // UploadDocumentCommand sets IsPublic = true, so that is nearly all of them.
        var principal = Principal(UserA);
        principal.FindFirst(ApplicationClaimTypes.TenantId).Should().BeNull(
            "the premise of this test is a principal with no tenant claim");

        (await IsPermitted(KeyInOtherTenant, principal, _permitAll)).Should().BeFalse();
    }

    [Test]
    public async Task AUserWhoHASSwitchedTenant_StillWorks()
    {
        // The population for whom reading the claim was correct, and the regression the fix could
        // plausibly have introduced. The tenant now comes from the loader, so a principal carrying a
        // claim as well must behave identically - and the claim must not be able to contradict the
        // user row, which is the stronger property: even a principal claiming tenant-2 is confined
        // to the tenant its row says it is in.
        var switched = PrincipalWithTenantClaim(UserA, OtherTenantId);

        (await IsPermitted(KeyInOtherTenant, switched, _permitAll)).Should().BeFalse(
            "the user row says tenant-1; a stale or forged claim must not widen that");
        (await IsPermitted(PublicKeyOfA, switched, _permitAll)).Should().BeTrue(
            "and their own tenant's documents are still served");
    }

    [Test]
    public async Task WhenTheLoaderCannotResolveThePrincipal_NothingIsServed()
    {
        // Fail closed, asserted rather than assumed. A null from the loader means the principal
        // could not be turned into a user at all - deleted account, unreachable database. The only
        // alternative was to fall back to ownership-and-publicity, which is the behaviour this pass
        // repaired, so it is not an alternative.
        var unresolvable = new Loader(new Dictionary<string, string?>());

        (await IsPermitted(PublicKeyOfA, Principal(UserA), _permitAll, unresolvable)).Should().BeFalse();
        (await IsPermitted(PrivateKeyOfA, Principal(UserA), _permitAll, unresolvable)).Should().BeFalse(
            "not even their own document, because we no longer know who they are");
    }

    [Test]
    public async Task AGenuinelyTenantlessPrincipal_KeepsTheSpecificationsDocumentedBehaviour()
    {
        // A resolvable user who genuinely has no tenant is a different thing from an unresolvable
        // one, and VisibleDocumentSpecification's no-tenant branch is written for exactly this case:
        // confined by ownership and publicity alone. The specification is deliberately unchanged -
        // four other consumers depend on that branch - so this asserts the distinction the fix
        // draws rather than a behaviour it invented.
        var tenantless = new Loader(new Dictionary<string, string?> { [UserA] = null });

        (await IsPermitted(PublicKeyOfA, Principal(UserA), _permitAll, tenantless)).Should().BeTrue();
        (await IsPermitted(PrivateKeyOfA, Principal(UserA), _permitAll, tenantless)).Should().BeTrue(
            "their own private document, by ownership");
    }

    [Test]
    public async Task AProfilePictureIsStillServedWithoutResolvingATenant()
    {
        // The avatar path returns before any tenant is needed, so an unresolvable principal must not
        // break it - seven render sites show other users' pictures.
        var unresolvable = new Loader(new Dictionary<string, string?>());

        (await IsPermitted($"ProfilePictures/{UserA}/avatar.jpg", Principal(UserB), _denyAll, unresolvable))
            .Should().BeTrue();
    }
}
#nullable restore
