#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Application.Features.Branches;
using CleanArchitecture.Blazor.Infrastructure.Services;
using CleanArchitecture.Blazor.Server.UI.Pages.Branches;
using CleanArchitecture.Blazor.Server.UI.Services;
using CleanArchitecture.Blazor.Server.UI.Services.Branches;
using CleanArchitecture.Blazor.Server.UI.Services.Navigation;
using FluentAssertions;
using FluentValidation;
using Mapster;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests.Extensions;

/// <summary>A module's own branch dialog (row 14): takes the command under the name the page passes it.</summary>
public sealed class TestModuleBranchDialog : ComponentBase
{
    [Parameter] public AddEditBranchCommand Model { get; set; } = default!;

    protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder) =>
        builder.AddMarkupContent(0, $"<div data-test=\"module-branch-dialog\">module dialog for '{Model.Code}'</div>");
}

/// <summary>Not a valid branch dialog: no Model parameter.</summary>
public sealed class NotABranchDialog : ComponentBase;

/// <summary>A module's tightening of the branch rules (row 13), as the module's Application folder would declare it.</summary>
public sealed class ThreeLetterCodeValidator : AbstractValidator<AddEditBranchCommand>
{
    public const string Message = "A branch code is exactly three capital letters.";
    public ThreeLetterCodeValidator() => RuleFor(c => c.Code).Matches("^[A-Z]{3}$").WithMessage(Message);
}

/// <summary>Pass 60b plan §5 rows 13 and 14: the branch screens (item 4c, 4d).</summary>
[TestFixture]
public class BranchScreenTests
{
    private BunitContext _ctx = null!;

    private void Start(Action<BranchScreenOptions>? configure = null, bool withModuleValidator = false)
    {
        _ctx = new BunitContext();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var services = _ctx.Services;
        services.AddLogging();
        services.AddLocalization();
        services.AddMudServices();
        services.AddSingleton(Mock.Of<IUserProfileState>());
        services.AddSingleton(Mock.Of<IApplicationSettings>());
        services.AddSingleton(Mock.Of<IAuthorizationService>());
        services.AddScoped<IValidationService>(sp => new ValidationService(sp)); // scoped, as the application registers it
        services.AddScoped<IValidator<AddEditBranchCommand>, AddEditBranchCommandValidator>();
        if (withModuleValidator) services.AddScoped<IValidator<AddEditBranchCommand>, ThreeLetterCodeValidator>();
        var mediator = new Mock<IMediator>();
        mediator.Setup(m => m.Send(It.IsAny<GetAllBranchesQuery>(), default)).ReturnsAsync(Array.Empty<BranchDto>());
        services.AddSingleton(mediator.Object);
        services.AddScoped<DialogServiceHelper>();
        services.AddSingleton(Mock.Of<IAppCache>());
        var permissions = new Mock<IPermissionService>();
        permissions.Setup(p => p.GetAccessRightsAsync<BranchesAccessRights>())
            .ReturnsAsync(new BranchesAccessRights { View = true, Create = true, Edit = true });
        services.AddSingleton(permissions.Object);
        services.AddSingleton(Mock.Of<IObjectMapper>());
        services.AddSingleton(new TypeAdapterConfig());
        services.AddOptions<BranchScreenOptions>();
        if (configure is not null) services.Configure(configure);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_ctx is not null) await _ctx.DisposeAsync();
    }

    // ---- 13 --------------------------------------------------------------------------------------------

    [Test]
    public async Task T13_TheDialog_ShowsAModulesRuleOnTheField()
    {
        Start(withModuleValidator: true);
        var provider = _ctx.Render<MudDialogProvider>();
        var model = new AddEditBranchCommand { Code = "AB", Name = "North" };
        var dialog = await provider.InvokeAsync(() => _ctx.Services.GetRequiredService<IDialogService>().ShowAsync<BranchFormDialog>(
            "New branch", new DialogParameters<BranchFormDialog> { { x => x.Model, model } }));

        await provider.FindAll("button").Single(b => b.TextContent.Contains("Save")).ClickAsync(new());

        provider.WaitForAssertion(() => provider.Markup.Should().Contain(ThreeLetterCodeValidator.Message));
        dialog.Result.IsCompleted.Should().BeFalse("an invalid form is not sent");
    }

    // ---- 14 --------------------------------------------------------------------------------------------

    [Test]
    public void T14_WithAPageRoute_TheTemplatePageRedirects_AndTheMenuPointsThere()
    {
        Start(o => o.PageRoute = "/org/branches");
        _ctx.Render<Branches>();

        _ctx.Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith("/org/branches");
        var options = _ctx.Services.GetRequiredService<IOptions<BranchScreenOptions>>();
        var menu = new MenuService(Mock.Of<IIdleTimeoutSettings>(s => s.Enabled == true), [], options);
        BranchesEntry(menu).Href.Should().Be("/org/branches");
    }

    [Test]
    public async Task T14_TheTemplatePage_OpensTheConfiguredDialog()
    {
        Start(o => o.FormDialog = typeof(TestModuleBranchDialog));
        var provider = _ctx.Render<MudDialogProvider>();
        var page = _ctx.Render<Branches>();

        _ = page.Find("[data-testid=branch-new]").ClickAsync(new()); // not awaited: the page awaits the dialog's result

        provider.WaitForAssertion(() => provider.Find("[data-test=module-branch-dialog]").TextContent.Should().Be("module dialog for ''"));
    }

    [Test]
    public async Task T14_WithoutOptions_EverythingIsAsIn170()
    {
        Start();
        var provider = _ctx.Render<MudDialogProvider>();
        var page = _ctx.Render<Branches>();

        _ctx.Services.GetRequiredService<NavigationManager>().Uri.Should().NotContain("/org/");
        _ = page.Find("[data-testid=branch-new]").ClickAsync(new()); // not awaited: the page awaits the dialog's result
        provider.WaitForAssertion(() => provider.FindAll("[data-testid=branch-code]").Should().NotBeEmpty("the template's dialog"));
        BranchesEntry(new MenuService(Mock.Of<IIdleTimeoutSettings>(s => s.Enabled == true))).Href.Should().Be(BranchScreenOptions.TemplateRoute);
    }

    [Test]
    public void T14_ADialogWithoutAModel_AndARouteNoPageAnswers_FailStartup()
    {
        var validator = new BranchScreenOptionsValidator();

        validator.Validate(null, new BranchScreenOptions()).Succeeded.Should().BeTrue("the defaults are the template's screens");
        validator.Validate(null, new BranchScreenOptions { FormDialog = typeof(NotABranchDialog) }).FailureMessage
            .Should().Contain("has no public [Parameter] AddEditBranchCommand Model");
        validator.Validate(null, new BranchScreenOptions { FormDialog = typeof(string) }).FailureMessage
            .Should().Contain("is not a Razor component");
        validator.Validate(null, new BranchScreenOptions { PageRoute = "/nowhere/branches" }).FailureMessage
            .Should().Contain("'/nowhere/branches' matches no @page");
        validator.Validate(null, new BranchScreenOptions { PageRoute = "/identity/users" }).Succeeded.Should().BeTrue();
    }

    private static Models.NavigationMenu.MenuSectionSubItemModel BranchesEntry(MenuService menu) =>
        menu.Features.SelectMany(s => s.SectionItems ?? []).SelectMany(i => i.MenuItems ?? []).Single(i => i.Title == "Branches");
}
