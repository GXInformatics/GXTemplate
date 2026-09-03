#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ardalis.Specification.EntityFrameworkCore;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Storage;
using CleanArchitecture.Blazor.Application.Common.Mappings;
using CleanArchitecture.Blazor.Application.Common.Models;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Application.Features.Documents.Commands.AddEdit;
using CleanArchitecture.Blazor.Application.Features.Documents.Commands.Delete;
using CleanArchitecture.Blazor.Application.Features.Documents.Specifications;
using CleanArchitecture.Blazor.Domain.Common.Enums;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Configurations;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Services.Storage;
using FluentAssertions;
using Mapster;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Moq;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Features.Documents;

/// <summary>
/// Documents is the one area this template describes as tenant-scoped. It was scoped on two of its
/// five entry points; these are the other three.
/// </summary>
/// <remarks>
/// Each test here failed before Pass 24 and states, in its own remarks, what it saw when it did.
/// The three defects were independent of one another and shared a cause: the visibility rule was
/// applied where somebody remembered to apply it, and the listing, the delete and the edit had each
/// been written without it.
/// <para>
/// Two tenants, two users, and a real file per document through the real disk storage provider -
/// deleting a document deletes its stored object too, so "the row survived" is only half of what a
/// refused delete has to prove.
/// </para>
/// </remarks>
[TestFixture]
public class DocumentTenantIsolationTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private const string UserA = "user-a";
    private const string UserA2 = "user-a2";
    private const string UserB = "user-b";

    private SqliteConnection _connection = null!;
    private ApplicationDbContext _db = null!;
    private string _fileRoot = null!;
    private IFileStorage _fileStorage = null!;

    private int _publicDocOfB;
    private int _privateDocOfA2;
    private int _publicDocOfA;
    private string _storageKeyOfB = null!;

    [SetUp]
    public async Task SetUp()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        _db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();

        _fileRoot = Path.Combine(Path.GetTempPath(), "gx-doc-isolation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_fileRoot);
        _fileStorage = new LocalDiskFileStorage(new StorageSettings { RootPath = _fileRoot });

        _db.Tenants.Add(new Tenant { Id = TenantA, Name = "Tenant A" });
        _db.Tenants.Add(new Tenant { Id = TenantB, Name = "Tenant B" });
        _db.Users.Add(new ApplicationUser { Id = UserA, UserName = "a", Email = "a@x.com", TenantId = TenantA });
        _db.Users.Add(new ApplicationUser { Id = UserA2, UserName = "a2", Email = "a2@x.com", TenantId = TenantA });
        _db.Users.Add(new ApplicationUser { Id = UserB, UserName = "b", Email = "b@x.com", TenantId = TenantB });
        await _db.SaveChangesAsync();

        // Everything is created "today" so the TODAY list view sees all of it on date alone - which
        // is precisely what that view used to filter on, and only that.
        (_publicDocOfB, _storageKeyOfB) = await AddDocumentAsync("b-public", TenantB, UserB, isPublic: true);
        (_privateDocOfA2, _) = await AddDocumentAsync("a2-private", TenantA, UserA2, isPublic: false);
        (_publicDocOfA, _) = await AddDocumentAsync("a-public", TenantA, UserA, isPublic: true);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
        try { Directory.Delete(_fileRoot, recursive: true); } catch (IOException) { }
    }

    private async Task<(int Id, string StorageKey)> AddDocumentAsync(
        string title, string tenantId, string ownerId, bool isPublic)
    {
        var stored = await _fileStorage.SaveAsync(new FileUploadRequest(
            $"{Guid.NewGuid():N}.txt", UploadType.Document, System.Text.Encoding.UTF8.GetBytes(title)));
        stored.Succeeded.Should().BeTrue(stored.ErrorMessage);

        var document = new Document
        {
            Title = title,
            IsPublic = isPublic,
            TenantId = tenantId,
            CreatedById = ownerId,
            CreatedAt = DateTime.UtcNow,
            StorageKey = stored.Data!.StorageKey,
            PublicUrl = stored.Data.PublicUrl
        };
        _db.Documents.Add(document);
        await _db.SaveChangesAsync();
        return (document.Id, document.StorageKey!);
    }

    private ApplicationDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);

    private Mock<IApplicationDbContextFactory> Factory()
    {
        var factory = new Mock<IApplicationDbContextFactory>();
        factory.Setup(x => x.CreateAsync(It.IsAny<CancellationToken>()))
            .Returns(() => new ValueTask<IApplicationDbContext>(NewContext()));
        return factory;
    }

    private static Mock<IUserContextAccessor> Accessor(string userId, string tenantId)
    {
        var accessor = new Mock<IUserContextAccessor>();
        accessor.SetupGet(x => x.Current).Returns(new UserContext(userId, userId, TenantId: tenantId));
        return accessor;
    }

    private static UserProfile Profile(string userId, string tenantId) =>
        new(userId, userId, $"{userId}@x.com", TenantId: tenantId, TimeZoneId: "UTC");

    // ---- B.1: the listing ----------------------------------------------------------------------

    private async Task<string[]> ListAsync(DocumentListView view, string userId, string tenantId)
    {
        var filter = new AdvancedDocumentsFilter
        {
            ListView = view,
            CurrentUser = Profile(userId, tenantId)
        };

        await using var db = NewContext();
        var titles = await db.Documents
            .WithSpecification(new AdvancedDocumentsSpecification(filter))
            .Select(x => x.Title!)
            .ToListAsync();
        return titles.OrderBy(x => x).ToArray();
    }

    [Test]
    public async Task TheTodayListView_ShowsNoOtherTenantsDocuments_AndNoOtherUsersPrivateOnes()
    {
        // RED before Pass 24: ["a-public", "a2-private", "b-public"].
        //
        // The TODAY branch of AdvancedDocumentsSpecification filtered on the date range and nothing
        // else - no tenant clause and no owner clause - so choosing "Created today" from the list
        // view dropdown returned every tenant's documents, private ones included, on a page whose
        // own download button would have refused to open them.
        var visible = await ListAsync(DocumentListView.TODAY, UserA, TenantA);

        visible.Should().Equal("a-public");
    }

    [Test]
    public async Task TheLast30DaysListView_IsScopedTheSameWay()
    {
        // The same defect, in the view that is the page's DEFAULT for system logs and one click away
        // here. Both date views were written the same way and both were missing the same clause.
        var visible = await ListAsync(DocumentListView.LAST_30_DAYS, UserA, TenantA);

        visible.Should().Equal("a-public");
    }

    [Test]
    public async Task EveryListView_AgreesWithTheDownloadRule()
    {
        // The structural claim, rather than one more example: no list view may show a document that
        // VisibleDocumentSpecification - the rule the download button and the /files endpoint
        // enforce - would refuse to serve. A page that lists what it cannot open is the symptom the
        // three tests above are each one instance of.
        await using var db = NewContext();
        var downloadable = await db.Documents
            .WithSpecification(new VisibleDocumentSpecification(UserA, TenantA))
            .Select(x => x.Title!)
            .ToListAsync();

        foreach (var view in Enum.GetValues<DocumentListView>())
        {
            var listed = await ListAsync(view, UserA, TenantA);
            listed.Should().BeSubsetOf(downloadable, $"the {view} view must not list what it cannot open");
        }
    }

    [Test]
    public async Task AUserStillSeesTheirOwnTenantsPublicDocuments()
    {
        // The other half of every scoping change: it must still show what it should. A rule that
        // returns nothing passes all three tests above and is useless.
        var visible = await ListAsync(DocumentListView.All, UserA2, TenantA);

        visible.Should().Equal("a-public", "a2-private");
    }

    // ---- B.2: delete ---------------------------------------------------------------------------

    private DeleteDocumentCommandHandler DeleteHandler(string userId, string tenantId) =>
        new(Factory().Object, Accessor(userId, tenantId).Object);

    [Test]
    public async Task ATenantsDocumentCannotBeDeletedFromAnotherTenant_AndItsStoredObjectSurvives()
    {
        // RED before Pass 24: the row was gone and the assertion on Documents.Count failed.
        //
        // DeleteDocumentCommandHandler resolved by id alone - Where(x => request.Id.Contains(x.Id)) -
        // so holding Permissions.Documents.Delete was the entire check. Worse than a read leak:
        // DocumentDeletedEvent removes the stored object too, so an id from another tenant destroyed
        // both the row and the file.
        var result = await DeleteHandler(UserA, TenantA)
            .Handle(new DeleteDocumentCommand([_publicDocOfB]), CancellationToken.None);

        // Reported exactly like deleting an id that does not exist, which is what deleting an
        // unreachable id already did. Saying "forbidden" instead would answer "does this id exist in
        // some other tenant?" for anyone who asked.
        result.Succeeded.Should().BeTrue();

        await using var db = NewContext();
        (await db.Documents.AnyAsync(x => x.Id == _publicDocOfB))
            .Should().BeTrue("the other tenant's document is still there");

        var stored = await _fileStorage.ReadAsync(_storageKeyOfB);
        stored.Succeeded.Should().BeTrue("and so is its stored object");
    }

    [Test]
    public async Task AUsersOwnDocumentIsStillDeletable()
    {
        var result = await DeleteHandler(UserA, TenantA)
            .Handle(new DeleteDocumentCommand([_publicDocOfA]), CancellationToken.None);

        result.Succeeded.Should().BeTrue();

        await using var db = NewContext();
        (await db.Documents.AnyAsync(x => x.Id == _publicDocOfA)).Should().BeFalse();
    }

    // ---- B.3: edit -----------------------------------------------------------------------------

    private AddEditDocumentCommandHandler EditHandler(string userId, string tenantId)
    {
        var localizer = new Mock<IStringLocalizer<AddEditDocumentCommandHandler>>();
        localizer.Setup(x => x[It.IsAny<string>()])
            .Returns((string name) => new LocalizedString(name, name));

        return new AddEditDocumentCommandHandler(
            Factory().Object,
            new MapsterObjectMapper(new TypeAdapterConfig()),
            Accessor(userId, tenantId).Object,
            localizer.Object);
    }

    [Test]
    public async Task ATenantsDocumentCannotBeEditedFromAnotherTenant()
    {
        // RED before Pass 24: Succeeded was true and the title had changed to "stolen".
        // FindAsync(request.Id) resolved by primary key with no visibility check at all.
        var result = await EditHandler(UserA, TenantA).Handle(
            new AddEditDocumentCommand { Id = _publicDocOfB, Title = "stolen" },
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();

        await using var db = NewContext();
        var document = await db.Documents.FindAsync(_publicDocOfB);
        document!.Title.Should().Be("b-public", "nothing about the other tenant's document changed");
    }

    [Test]
    public async Task AnEditCannotReParentADocumentIntoAnotherTenant()
    {
        // RED before Pass 24: TenantId became "tenant-b".
        //
        // The command carries a TenantId because the DTO it is mapped from does, and the mapper
        // copies it by name - so any edit of a document the caller COULD legitimately reach was also
        // an unrestricted move of that document into any tenant it named. Quieter than the two above
        // and, unlike them, it needed no other tenant's id to exploit.
        var result = await EditHandler(UserA, TenantA).Handle(
            new AddEditDocumentCommand { Id = _publicDocOfA, Title = "a-public", TenantId = TenantB },
            CancellationToken.None);

        result.Succeeded.Should().BeTrue("editing one's own document is allowed");

        await using var db = NewContext();
        var document = await db.Documents.FindAsync(_publicDocOfA);
        document!.TenantId.Should().Be(TenantA, "the tenant is not the caller's to set");
    }

    [Test]
    public async Task AnOrdinaryEditOfAnOwnDocumentStillWorks()
    {
        var result = await EditHandler(UserA, TenantA).Handle(
            new AddEditDocumentCommand { Id = _publicDocOfA, Title = "renamed", IsPublic = true },
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();

        await using var db = NewContext();
        var document = await db.Documents.FindAsync(_publicDocOfA);
        document!.Title.Should().Be("renamed");
        document.TenantId.Should().Be(TenantA);
    }
}
#nullable restore
