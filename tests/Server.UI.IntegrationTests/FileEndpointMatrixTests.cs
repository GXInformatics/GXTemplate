#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Storage;
using CleanArchitecture.Blazor.Application.Common.Models;
using CleanArchitecture.Blazor.Domain.Common.Enums;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// The /files authorization matrix, measured by hand in Pass 7C-2 §G.4 and now permanent.
/// </summary>
/// <remarks>
/// This is the matrix that replaced an anonymous static-file mount serving every uploaded document
/// and every avatar to anyone who could guess a path. The rows below are the evidence that it stays
/// replaced: bytes for an authorized caller, a challenge for an anonymous one, and 404 - not 403,
/// and not bytes - for an authenticated caller who is not allowed this particular object.
/// </remarks>
[TestFixture]
public class FileEndpointMatrixTests
{
    private GxWebApplicationFactory _factory = null!;
    private HttpClient _authenticated = null!;
    private HttpClient _anonymous = null!;

    private byte[] _avatarBytes = null!;
    private string _avatarKey = null!;
    private byte[] _documentBytes = null!;
    private string _visibleDocumentKey = null!;
    private string _orphanDocumentKey = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new GxWebApplicationFactory(Environments.Production);
        await _factory.ResetAdministratorPasswordAsync(mustChangePassword: false);

        using var scope = _factory.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var administrator = (await userManager.FindByNameAsync(Users.Administrator))!;

        // Stored through the real provider, so the key and the public URL are the ones the
        // application itself would have produced.
        _avatarBytes = Enumerable.Range(0, 256).Select(i => (byte)(255 - i)).ToArray();
        var avatar = await storage.SaveAsync(new FileUploadRequest(
            "avatar.jpg", UploadType.ProfilePicture, _avatarBytes, overwrite: true, folder: administrator.Id));
        avatar.Succeeded.Should().BeTrue(avatar.ErrorMessage);
        _avatarKey = avatar.Data!.StorageKey;

        _documentBytes = Enumerable.Range(0, 128).Select(i => (byte)(i * 2)).ToArray();
        var visible = await storage.SaveAsync(new FileUploadRequest(
            "invoice.png", UploadType.Document, _documentBytes, overwrite: true));
        _visibleDocumentKey = visible.Data!.StorageKey;

        // Bytes on disk with no Documents row granting them: the per-object check should refuse
        // this even though the object is right there and the caller holds Documents.Download.
        var orphan = await storage.SaveAsync(new FileUploadRequest(
            "someone-elses.png", UploadType.Document, new byte[] { 7, 7, 7 }, overwrite: true));
        _orphanDocumentKey = orphan.Data!.StorageKey;

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Documents.Add(new Document
        {
            Title = "invoice.png",
            IsPublic = true,
            TenantId = administrator.TenantId,
            CreatedById = administrator.Id,
            StorageKey = _visibleDocumentKey,
            PublicUrl = visible.Data.PublicUrl
        });
        await db.SaveChangesAsync();

        _anonymous = _factory.CreateNonRedirectingClient();
        _authenticated = _factory.CreateNonRedirectingClient();
        await CookieLogin.SignInAndExpectSuccessAsync(
            _authenticated, Users.Administrator, GxWebApplicationFactory.KnownPassword);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _anonymous.Dispose();
        _authenticated.Dispose();
        _factory.Dispose();
    }

    [Test]
    public async Task AnAvatar_IsServedToAnAuthenticatedCaller_ByteForByte()
    {
        var response = await CookieLogin.GetAsAssetAsync(_authenticated, "/files/" + _avatarKey);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(_avatarBytes);
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/jpeg");
    }

    [Test]
    public async Task AnAvatar_CarriesAPrivateCacheControlHeader()
    {
        // private, not public: these are per-principal authorized bytes, so a shared proxy must not
        // hold them. The max-age is what keeps an avatar to one fetch per cache lifetime rather
        // than one per render, which is the cost of moving off the old static route.
        var response = await CookieLogin.GetAsAssetAsync(_authenticated, "/files/" + _avatarKey);

        response.Headers.CacheControl!.Private.Should().BeTrue();
        response.Headers.CacheControl.MaxAge.Should().NotBeNull();
    }

    [Test]
    public async Task AnAvatar_ChallengesAnAnonymousCaller_AndReturnsNoBytes()
    {
        var response = await CookieLogin.GetAsAssetAsync(_anonymous, "/files/" + _avatarKey);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        (await response.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
    }

    [Test]
    public async Task TheOldStylePath_ServesNoBytesAnonymously_ButIsTheSameAuthorizedEndpoint()
    {
        var anonymous = await CookieLogin.GetAsAssetAsync(_anonymous, "/Files/" + _avatarKey);
        anonymous.StatusCode.Should().Be(HttpStatusCode.Found);
        (await anonymous.Content.ReadAsByteArrayAsync()).Should().BeEmpty();

        // Route matching is case-insensitive, so the old spelling reaches the new endpoint. That it
        // is the ENDPOINT and not a static mount is visible in the header: UseStaticFiles never set
        // Cache-Control: private.
        var authenticated = await CookieLogin.GetAsAssetAsync(_authenticated, "/Files/" + _avatarKey);
        authenticated.StatusCode.Should().Be(HttpStatusCode.OK);
        authenticated.Headers.CacheControl!.Private.Should().BeTrue();
    }

    [Test]
    public async Task AVisibleDocument_IsServedToItsOwner()
    {
        var response = await CookieLogin.GetAsAssetAsync(_authenticated, "/files/" + _visibleDocumentKey);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(_documentBytes);
    }

    [Test]
    public async Task ADocumentKeyWithNoVisibleRow_Is404_EvenThoughTheBytesExist()
    {
        // The per-object half of the endpoint's authorization. Document file names come from
        // whoever uploaded them, so they are guessable; authentication alone would have served this.
        var response = await CookieLogin.GetAsAssetAsync(_authenticated, "/files/" + _orphanDocumentKey);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task AKeyThatDoesNotResolve_Is404_AndLooksLikeARefusedOne()
    {
        var missing = await CookieLogin.GetAsAssetAsync(_authenticated, "/files/Documents/never-stored.png");

        // Identical to the refused case above, so keys cannot be probed by comparing responses.
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task ATraversalKey_IsRefused()
    {
        var response = await CookieLogin.GetAsAssetAsync(
            _authenticated, "/files/Documents/..%2F..%2Fappsettings.json");

        response.StatusCode.Should().NotBe(HttpStatusCode.OK);
    }
}
#nullable restore
