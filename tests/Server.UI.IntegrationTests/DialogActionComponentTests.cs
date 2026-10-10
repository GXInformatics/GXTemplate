#nullable enable
using System;
using System.Threading.Tasks;
using Bunit;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Server.UI.Components.Common;
using CleanArchitecture.Blazor.Server.UI.Components.Dialogs;
using CleanArchitecture.Blazor.Server.UI.Pages.Identity.Users.Components;
using CleanArchitecture.Blazor.Server.UI.Services;
using FluentAssertions;
using Mapster;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// Pass 52: a row action runs inside its dialog, with progress. <see cref="ConfirmationDialog"/> and
/// <see cref="ResetPasswordDialog"/> take an optional action that their confirm button runs through
/// <see cref="GxSubmitButton"/>: success closes the dialog; a failure shows its sentence in the dialog, which stays open;
/// Cancel is disabled while the action runs.
/// </summary>
[TestFixture]
public class DialogActionComponentTests
{
    private BunitContext _ctx = null!;
    private IRenderedComponent<MudDialogProvider> _provider = null!;

    [SetUp]
    public void SetUp()
    {
        _ctx = new BunitContext();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        // What Server.UI's _Imports.razor injects into every component.
        var services = _ctx.Services;
        services.AddLogging();
        services.AddLocalization();
        services.AddMudServices();
        services.AddSingleton(Mock.Of<IUserProfileState>());
        services.AddSingleton(Mock.Of<IApplicationSettings>());
        services.AddSingleton(Mock.Of<IAuthorizationService>());
        services.AddSingleton(Mock.Of<IValidationService>());
        services.AddSingleton(Mock.Of<IMediator>());
        services.AddScoped<DialogServiceHelper>();
        services.AddSingleton(Mock.Of<IAppCache>());
        services.AddSingleton(Mock.Of<IPermissionService>());
        services.AddSingleton(Mock.Of<IObjectMapper>());
        services.AddSingleton(new TypeAdapterConfig());

        _provider = _ctx.Render<MudDialogProvider>();
    }

    [TearDown]
    public async Task TearDown() => await _ctx.DisposeAsync();

    private Task<IDialogReference> ConfirmAsync(Func<Task<string?>> action) =>
        _provider.InvokeAsync(() => _ctx.Services.GetRequiredService<IDialogService>().ShowAsync<ConfirmationDialog>(
            "Reopen AWT-001",
            new DialogParameters<ConfirmationDialog>
            {
                { x => x.ContentText, "Reopen AWT-001? It will be awaiting again." },
                { x => x.Action, action },
                { x => x.BusyLabel, "Saving…" }
            }));

    [Test]
    public async Task AFailedAction_ShowsItsSentenceInTheDialog_WhichStaysOpen()
    {
        // M1's test.
        var dialog = await ConfirmAsync(() => Task.FromResult<string?>("The item was changed by someone else. Reload and try again."));

        await _provider.Find("[data-test=dialog-confirm]").ClickAsync(new());

        _provider.WaitForAssertion(() => _provider.Find("[data-test=dialog-failure]").TextContent
            .Should().Contain("changed by someone else"));
        _provider.FindAll("[data-test=dialog-confirm]").Should().ContainSingle("the dialog is still open, so the user can read why");
        dialog.Result.IsCompleted.Should().BeFalse("a failure does not close the dialog");
        _provider.Find("[data-test=dialog-cancel]").HasAttribute("disabled").Should().BeFalse("once the action is over, the user may give up");
    }

    [Test]
    public async Task WhileTheActionRuns_CancelIsDisabled_AndConfirmIsBusy_ThenSuccessClosesTheDialog()
    {
        var work = new TaskCompletionSource<string?>();
        var dialog = await ConfirmAsync(() => work.Task);

        var click = _provider.Find("[data-test=dialog-confirm]").ClickAsync(new());

        _provider.WaitForAssertion(() => _provider.Find("[data-test=dialog-cancel]").HasAttribute("disabled").Should().BeTrue());
        _provider.Find("[data-test=dialog-confirm]").ClassList.Should().Contain(GxSubmitButton.BusyClass);
        _provider.Find("[data-test=dialog-confirm] .gx-busy-text").TextContent.Should().Be("Saving…");

        work.SetResult(null);
        await click;

        var result = await dialog.Result.WaitAsync(TimeSpan.FromSeconds(10));
        result!.Canceled.Should().BeFalse("success closes the dialog as confirmed");
        _provider.WaitForAssertion(() => _provider.FindAll("[data-test=dialog-confirm]").Should().BeEmpty());
    }

    [Test]
    public async Task AValidationRefusal_IsShownLikeAnyOtherFailure()
    {
        await ConfirmAsync(() => throw new FluentValidation.ValidationException("The date is required."));

        await _provider.Find("[data-test=dialog-confirm]").ClickAsync(new());

        _provider.WaitForAssertion(() => _provider.Find("[data-test=dialog-failure]").TextContent.Should().Contain("The date is required."));
    }

    private Task<IDialogReference> ResetPasswordAsync(string password, Func<Task<string?>> action) =>
        _provider.InvokeAsync(() => _ctx.Services.GetRequiredService<IDialogService>().ShowAsync<ResetPasswordDialog>(
            "Set Password",
            new DialogParameters<ResetPasswordDialog>
            {
                { x => x.Model, new ResetPasswordDialog.ResetPasswordModel { UserId = "u1", UserName = "alice", Password = password, ConfirmPassword = password } },
                { x => x.Action, action }
            }));

    [Test]
    public async Task TheResetPasswordDialog_ResetsInsideTheDialog_AndKeepsARefusalThere()
    {
        // CO-117: the reset used to run after the dialog had closed, with no progress and the refusal in a snackbar.
        var ran = 0;
        var dialog = await ResetPasswordAsync("Str0ng!Passw0rd", () => { ran++; return Task.FromResult<string?>("Passwords must have at least one digit."); });

        await _provider.Find("[data-test=dialog-confirm]").ClickAsync(new());

        _provider.WaitForAssertion(() => _provider.Find("[data-test=dialog-failure]").TextContent.Should().Contain("at least one digit"));
        ran.Should().Be(1);
        dialog.Result.IsCompleted.Should().BeFalse("the password can be corrected and set again");
    }

    [Test]
    public async Task TheResetPasswordDialog_DoesNotRunTheReset_WhileTheFormIsInvalid()
    {
        var ran = 0;
        var dialog = await ResetPasswordAsync("short", () => { ran++; return Task.FromResult<string?>(null); });

        await _provider.Find("[data-test=dialog-confirm]").ClickAsync(new());

        ran.Should().Be(0, "an invalid password is refused by the form before any work starts");
        dialog.Result.IsCompleted.Should().BeFalse();
        _provider.FindAll("[data-test=dialog-failure]").Should().BeEmpty("the validation messages say why, beside the fields");
    }

    [Test]
    public async Task TheResetPasswordDialog_ClosesAsConfirmed_WhenTheResetSucceeds()
    {
        var dialog = await ResetPasswordAsync("Str0ng!Passw0rd", () => Task.FromResult<string?>(null));

        await _provider.Find("[data-test=dialog-confirm]").ClickAsync(new());

        var result = await dialog.Result.WaitAsync(TimeSpan.FromSeconds(10));
        result!.Canceled.Should().BeFalse();
    }
}
#nullable restore
