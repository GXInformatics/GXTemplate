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

    private static ClaimsPrincipal Principal(string userId, string tenantId) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(ApplicationClaimTypes.TenantId, tenantId)
        }, authenticationType: "Test"));

    private Task<bool> IsPermitted(string key, ClaimsPrincipal user, IAuthorizationService authorization) =>
        FileEndpoints.IsPermittedAsync(key, user, _factory, authorization, CancellationToken.None);

    [Test]
    public async Task AProfilePictureKey_NeedsOnlyAuthentication()
    {
        // Any authenticated user may fetch any avatar - the alternative breaks every grid and
        // presence list that shows somebody else's picture, and protects nothing that is not
        // already displayed next to that person's name.
        (await IsPermitted($"ProfilePictures/{UserA}/avatar.jpg", Principal(UserB, TenantId), _denyAll))
            .Should().BeTrue();
    }

    [Test]
    public async Task TheOwner_MayFetchTheirOwnPrivateDocument()
    {
        (await IsPermitted(PrivateKeyOfA, Principal(UserA, TenantId), _permitAll)).Should().BeTrue();
    }

    [Test]
    public async Task AnotherUserInTheSameTenant_MayNotFetchAPrivateDocument()
    {
        // Authentication alone would have served this. The file name is the uploader's own
        // ("invoice.png"), so guessing it is not a stretch.
        (await IsPermitted(PrivateKeyOfA, Principal(UserB, TenantId), _permitAll)).Should().BeFalse();
    }

    [Test]
    public async Task APublicDocument_IsReadableAcrossUsersInsideTheTenant()
    {
        (await IsPermitted(PublicKeyOfA, Principal(UserB, TenantId), _permitAll)).Should().BeTrue();
    }

    [Test]
    public async Task ADocumentInAnotherTenant_IsRefused_EvenThoughItIsPublic()
    {
        (await IsPermitted(KeyInOtherTenant, Principal(UserA, TenantId), _permitAll)).Should().BeFalse();
    }

    [Test]
    public async Task WithoutTheDownloadPermission_NoDocumentKeyIsServed()
    {
        (await IsPermitted(PublicKeyOfA, Principal(UserA, TenantId), _denyAll)).Should().BeFalse();
    }

    [Test]
    public async Task ADocumentKeyWithNoMatchingRow_IsRefused()
    {
        (await IsPermitted("Documents/never-existed.png", Principal(UserA, TenantId), _permitAll))
            .Should().BeFalse();
    }

    [TestCase("documents/private-of-a.png")]
    [TestCase("Documents\\private-of-a.png")]
    public async Task TheDocumentPrefixIsMatchedRegardlessOfCasingOrSeparator(string key)
    {
        // The prefix test is what decides whether the per-object check runs at all, so a key that
        // differs only in casing or slash direction must not slip past it.
        (await IsPermitted(key, Principal(UserB, TenantId), _permitAll)).Should().BeFalse();
    }
}
#nullable restore
