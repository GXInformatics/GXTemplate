#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Storage;
using CleanArchitecture.Blazor.Application.Common.Models;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Server.UI.Components.Inputs.Autocomplete;
using CleanArchitecture.Blazor.Server.UI.Pages.Identity.Users.Components;
using CleanArchitecture.Blazor.Server.UI.Services;
using CleanArchitecture.Blazor.Server.UI.Services.Layout;
using CleanArchitecture.Blazor.Server.UI.Services.UserPreferences;
using FluentAssertions;
using Mapster;
using Mediator;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor.Services;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// Pass 47b: a refused photo upload on the profile tab says why, where the user uploaded, and the photo stays as it
/// was. Before, <c>ImageProcessor</c>'s refusal escaped the upload handler into the circuit and the app's error screen.
/// </summary>
[TestFixture]
public class ProfilePhotoUploadComponentTests
{
    private const string StoredPhoto = "/files/ProfilePictures/u-basic/legacy-avatar.jpg";

    private BunitContext _ctx = null!;
    private Mock<IFileStorage> _storage = null!;
    private Mock<IUserProfileState> _profile = null!;

    [SetUp]
    public void SetUp()
    {
        _ctx = new BunitContext();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var services = _ctx.Services;
        services.AddLogging();
        services.AddLocalization();
        services.AddMudServices();
        services.AddSingleton(Mock.Of<IUserPreferencesService>());
        services.AddScoped<LayoutService>();
        services.AddScoped<DialogServiceHelper>();
        services.AddSingleton(new TypeAdapterConfig());
        services.AddSingleton(Mock.Of<IApplicationSettings>());
        services.AddSingleton(Mock.Of<IValidationService>());
        services.AddSingleton(Mock.Of<IMediator>());
        services.AddSingleton(Mock.Of<IAppCache>());
        services.AddSingleton(Mock.Of<IPermissionService>());
        services.AddSingleton(Mock.Of<IObjectMapper>());
        services.AddSingleton(Mock.Of<Microsoft.AspNetCore.Authorization.IAuthorizationService>());
        services.AddSingleton(Mock.Of<Microsoft.AspNetCore.Identity.IUserStore<CleanArchitecture.Blazor.Domain.Identity.ApplicationUser>>());
        services.AddIdentityCore<CleanArchitecture.Blazor.Domain.Identity.ApplicationUser>();

        _profile = new Mock<IUserProfileState>();
        _profile.SetupGet(p => p.Value).Returns(UserProfile.Empty with
        {
            UserId = "u-basic", UserName = "basic.member", Email = "basic@example.com", ProfilePictureDataUrl = StoredPhoto
        });
        services.AddSingleton(_profile.Object);

        _storage = new Mock<IFileStorage>();
        services.AddSingleton(_storage.Object);

        // The two pickers are not under test and want their own services.
        _ctx.ComponentFactories.AddStub(t => t.IsGenericType &&
            (t.GetGenericTypeDefinition() == typeof(TimeZoneAutocomplete<>) || t.GetGenericTypeDefinition() == typeof(LanguageAutocomplete<>)));
    }

    [TearDown]
    public async Task TearDown() => await _ctx.DisposeAsync();

    /// <summary>A real 1 × 1 GIF: decodable, and refused for its format.</summary>
    private static readonly byte[] Gif =
    [
        0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x21,
        0xF9, 0x04, 0x01, 0x00, 0x00, 0x00, 0x00, 0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x02, 0x02, 0x44,
        0x01, 0x00, 0x3B
    ];

    // S3 (also red under X1: a GIF decoded is no longer refused)
    [TestCase("photo.gif", "Only JPEG, PNG and WebP images can be uploaded.")]
    [TestCase("photo.jpg", "Only JPEG, PNG and WebP images can be uploaded.")] // a GIF named .jpg: judged by content
    public void ARefusedUpload_ShowsItsMessage_UnderThePhoto_AndThePhotoStays(string name, string message)
    {
        var tab = _ctx.Render<ProfileInformationTab>();
        tab.FindAll("[data-test=photo-upload-error]").Should().BeEmpty();

        tab.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(Gif, name, contentType: "image/gif"));

        tab.WaitForAssertion(() => tab.Find("[data-test=photo-upload-error]").TextContent.Trim().Should().Be(message));
        tab.FindAll("img").Select(i => i.GetAttribute("src")).Should().Contain(StoredPhoto, "the photo stays as it was");
        _storage.Verify(s => s.SaveAsync(It.IsAny<FileUploadRequest>(), It.IsAny<CancellationToken>()), Times.Never());
        _profile.Verify(p => p.UpdateAsync(It.IsAny<Func<UserProfile, UserProfile>>()), Times.Never());
    }

    [Test]
    public void ACorruptJpeg_IsRefusedTheSameWay()
    {
        // A JPEG signature with nothing a decoder can read.
        var corrupt = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 }.Concat(new byte[200]).ToArray();
        var tab = _ctx.Render<ProfileInformationTab>();

        tab.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(corrupt, "photo.jpg", contentType: "image/jpeg"));

        tab.WaitForAssertion(() => tab.Find("[data-test=photo-upload-error]").TextContent.Trim().Should().Be("The image could not be read."));
        _storage.Verify(s => s.SaveAsync(It.IsAny<FileUploadRequest>(), It.IsAny<CancellationToken>()), Times.Never());
    }
}
#nullable restore
