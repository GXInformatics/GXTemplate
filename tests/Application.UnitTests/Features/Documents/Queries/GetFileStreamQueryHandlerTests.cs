#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Storage;
using CleanArchitecture.Blazor.Application.Common.Models;
using CleanArchitecture.Blazor.Domain.Common.Enums;
using CleanArchitecture.Blazor.Infrastructure.Configurations;
using CleanArchitecture.Blazor.Infrastructure.Services.Storage;
using CleanArchitecture.Blazor.Application.Features.Documents.Queries.GetFileStream;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Features.Documents.Queries;

/// <summary>
/// Regression tests for the document visibility rule and the principal-scoped cache key on
/// <see cref="GetFileStreamQuery"/>. Before the fix the handler fetched by primary key with no
/// ownership check, and the cache key was id-only, so cached bytes leaked between users.
/// </summary>
[TestFixture]
public class GetFileStreamQueryHandlerTests
{
    private const string TenantId = "tenant-1";
    private const string UserA = "user-a";
    private const string UserB = "user-b";

    private SqliteConnection _connection = null!;
    private ApplicationDbContext _db = null!;
    private string _fileRoot = null!;
    private IFileStorage _fileStorage = null!;

    private int _privateDocOfA;
    private int _publicDocOfA;

    [SetUp]
    public async Task SetUp()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new ApplicationDbContext(options);
        await _db.Database.EnsureCreatedAsync();

        // The handler no longer touches the filesystem itself - it reads through IFileStorage. The
        // real disk provider is used here, rooted at a throwaway directory, so these tests exercise
        // the actual key -> bytes path rather than a stub of it.
        _fileRoot = Path.Combine(Path.GetTempPath(), "gx-storage-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_fileRoot);
        _fileStorage = new LocalDiskFileStorage(new StorageSettings { RootPath = _fileRoot });

        _db.Tenants.Add(new Tenant { Id = TenantId, Name = "Tenant One" });
        _db.Users.Add(new ApplicationUser { Id = UserA, UserName = "a", Email = "a@example.com", TenantId = TenantId });
        _db.Users.Add(new ApplicationUser { Id = UserB, UserName = "b", Email = "b@example.com", TenantId = TenantId });
        await _db.SaveChangesAsync();

        _privateDocOfA = await AddDocumentAsync(isPublic: false, ownerId: UserA, contents: "private-bytes");
        _publicDocOfA = await AddDocumentAsync(isPublic: true, ownerId: UserA, contents: "public-bytes");
    }

    [TearDown]
    public async Task TearDown()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();

        if (Directory.Exists(_fileRoot))
        {
            Directory.Delete(_fileRoot, recursive: true);
        }
    }

    private async Task<int> AddDocumentAsync(bool isPublic, string ownerId, string contents)
    {
        var stored = await _fileStorage.SaveAsync(new FileUploadRequest(
            $"{Guid.NewGuid():N}.txt", UploadType.Document, System.Text.Encoding.UTF8.GetBytes(contents)));
        stored.Succeeded.Should().BeTrue();

        var document = new Document
        {
            Title = isPublic ? "public" : "private",
            IsPublic = isPublic,
            TenantId = TenantId,
            CreatedById = ownerId,
            StorageKey = stored.Data!.StorageKey,
            PublicUrl = stored.Data.PublicUrl
        };
        _db.Documents.Add(document);
        await _db.SaveChangesAsync();
        return document.Id;
    }

    private ApplicationDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);

    private GetFileStreamQueryHandler CreateHandler(UserContext? ambientUser)
    {
        // The handler owns and disposes the context it is handed, so every call gets a fresh one over
        // the same open SQLite connection (and therefore the same in-memory database).
        var factory = new Mock<IApplicationDbContextFactory>();
        factory.Setup(x => x.CreateAsync(It.IsAny<CancellationToken>()))
            .Returns(() => new ValueTask<IApplicationDbContext>(NewContext()));

        var accessor = new Mock<IUserContextAccessor>();
        accessor.SetupGet(x => x.Current).Returns(ambientUser);

        return new GetFileStreamQueryHandler(factory.Object, accessor.Object, _fileStorage);
    }

    private static UserContext Context(string userId) =>
        new(userId, userId, TenantId: TenantId);

    private static GetFileStreamQuery Query(int id, string userId) =>
        new(id, userId, TenantId);

    [Test]
    public async Task Handle_UploadedBytes_ComeBackByteIdentical()
    {
        // The A4 closure. Upload through the same abstraction the upload command uses, download
        // through the query the download button uses, and compare bytes. On the pre-pass code the
        // handler rebuilt a filesystem path from a field that could be an absolute https:// URL and
        // returned (string.Empty, Array.Empty<byte>()) when that path did not exist - a completely
        // broken download that looked exactly like an empty file.
        var payload = new byte[512];
        new Random(20260826).NextBytes(payload);

        var stored = await _fileStorage.SaveAsync(new FileUploadRequest(
            $"{Guid.NewGuid():N}.bin", UploadType.Document, payload));
        stored.Succeeded.Should().BeTrue(stored.ErrorMessage);

        var document = new Document
        {
            Title = "binary",
            IsPublic = true,
            TenantId = TenantId,
            CreatedById = UserA,
            StorageKey = stored.Data!.StorageKey,
            PublicUrl = stored.Data.PublicUrl
        };
        _db.Documents.Add(document);
        await _db.SaveChangesAsync();

        var handler = CreateHandler(Context(UserA));
        var (fileName, bytes) = await handler.Handle(Query(document.Id, UserA), CancellationToken.None);

        bytes.Should().Equal(payload);
        fileName.Should().Be(stored.Data.FileName);
    }

    [Test]
    public async Task Handle_WhenTheStoredObjectIsMissing_IsReported_NotSilentlyEmpty()
    {
        // The other half of A4: a key that does not resolve must be an error the caller sees, never
        // a successful read of zero bytes.
        var document = new Document
        {
            Title = "dangling",
            IsPublic = true,
            TenantId = TenantId,
            CreatedById = UserA,
            StorageKey = "Documents/never-stored.bin"
        };
        _db.Documents.Add(document);
        await _db.SaveChangesAsync();

        var handler = CreateHandler(Context(UserA));

        var act = async () => await handler.Handle(Query(document.Id, UserA), CancellationToken.None);

        await act.Should().ThrowAsync<Exception>()
            .WithMessage("*could not read the stored file*");
    }

    [Test]
    public async Task Handle_OwnerOfPrivateDocument_ReturnsFileBytes()
    {
        var handler = CreateHandler(Context(UserA));

        var (fileName, bytes) = await handler.Handle(Query(_privateDocOfA, UserA), CancellationToken.None);

        fileName.Should().EndWith(".txt");
        System.Text.Encoding.UTF8.GetString(bytes).Should().Be("private-bytes");
    }

    [Test]
    public async Task Handle_OtherUserInSameTenant_CannotReadPrivateDocument()
    {
        var handler = CreateHandler(Context(UserB));

        var act = async () => await handler.Handle(Query(_privateDocOfA, UserB), CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
    }

    [Test]
    public async Task Handle_PublicDocument_IsReadableByBothUsers()
    {
        var owner = CreateHandler(Context(UserA));
        var other = CreateHandler(Context(UserB));

        var fromOwner = await owner.Handle(Query(_publicDocOfA, UserA), CancellationToken.None);
        var fromOther = await other.Handle(Query(_publicDocOfA, UserB), CancellationToken.None);

        System.Text.Encoding.UTF8.GetString(fromOwner.Item2).Should().Be("public-bytes");
        System.Text.Encoding.UTF8.GetString(fromOther.Item2).Should().Be("public-bytes");
    }

    [Test]
    public async Task Handle_ForbiddenDocument_IsIndistinguishableFromMissingDocument()
    {
        var handler = CreateHandler(Context(UserB));

        var forbidden = await CaptureMessageAsync(handler, Query(_privateDocOfA, UserB));
        var missing = await CaptureMessageAsync(handler, Query(999_999, UserB));

        // Both report the same "not found" template, so a document that exists but is invisible cannot
        // be told apart from one that does not exist - ids cannot be enumerated by comparing responses.
        forbidden.Should().Be($"not found document entry by Id:{_privateDocOfA}.");
        missing.Should().Be("not found document entry by Id:999999.");
    }

    [Test]
    public async Task Handle_WithoutAmbientUserContext_IsDenied()
    {
        var handler = CreateHandler(ambientUser: null);

        var act = async () => await handler.Handle(Query(_publicDocOfA, UserA), CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
    }

    [Test]
    public async Task Handle_WhenCarriedPrincipalDisagreesWithAmbientUser_IsDenied()
    {
        // The cache key is built from the carried principal. Serving this would file user A's bytes
        // under user B's cache key.
        var handler = CreateHandler(Context(UserA));

        var act = async () => await handler.Handle(Query(_publicDocOfA, UserB), CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
    }

    [Test]
    public void TheEffectiveCacheKeyIsDistinctPerPrincipal()
    {
        // The guarantee is unchanged - two principals can never share an entry for the same document
        // - but the pipeline now provides it. The query declares PerUserAndTenant and the caching
        // behaviour folds the AMBIENT user and tenant in, so the separation no longer depends on the
        // calling page having passed the right principal into the request.
        var query = new GetFileStreamQuery(_privateDocOfA, UserA, TenantId);
        query.Scope.Should().Be(CacheScope.PerUserAndTenant);

        var forA = CacheScopeKey.Compose(query.CacheKey, query.Scope, Context(UserA, TenantId));
        var forB = CacheScopeKey.Compose(query.CacheKey, query.Scope, Context(UserB, TenantId));
        var forAInOtherTenant = CacheScopeKey.Compose(query.CacheKey, query.Scope, Context(UserA, "tenant-2"));

        forA.Should().NotBe(forB, "a different user must not reach the same entry");
        forA.Should().NotBe(forAInOtherTenant, "a different tenant must not reach the same entry");
        forA.Should().Contain(UserA).And.Contain(TenantId);
    }

    [Test]
    public void TheDeclaredCacheKeyNoLongerCarriesThePrincipalByHand()
    {
        // Recorded deliberately: the principal moved OUT of the declared key and INTO the scope.
        // Reintroducing it here would fold it in twice.
        var query = new GetFileStreamQuery(_privateDocOfA, UserA, TenantId);

        query.CacheKey.Should().NotContain(UserA).And.NotContain(TenantId);
        query.CacheKey.Should().Contain(_privateDocOfA.ToString());
    }

    private static UserContext Context(string userId, string tenantId) =>
        new(UserId: userId, UserName: userId, TenantId: tenantId);

    private static async Task<string> CaptureMessageAsync(GetFileStreamQueryHandler handler, GetFileStreamQuery query)
    {
        try
        {
            await handler.Handle(query, CancellationToken.None);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }

        throw new AssertionException("Expected the handler to deny the request.");
    }
}
#nullable restore
