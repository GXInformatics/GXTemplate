#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Models;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Application.Features.PicklistSets;
using CleanArchitecture.Blazor.Application.Features.PicklistSets.DTOs;
using CleanArchitecture.Blazor.Application.Features.PicklistSets.Queries.PaginationQuery;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Infrastructure.Configurations;
using CleanArchitecture.Blazor.Server.UI.Pages.PicklistSets;
using CleanArchitecture.Blazor.Server.UI.Services;
using CleanArchitecture.Blazor.Server.UI.Services.JsInterop;
using CleanArchitecture.Blazor.Server.UI.Services.Layout;
using CleanArchitecture.Blazor.Server.UI.Services.UserPreferences;
using FluentAssertions;
using Mapster;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor.Services;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// What the picklist grid offers over a SHARED row, with and without
/// <c>Permissions.PicklistSets.ManageShared</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only rendering can see this.</b> The application renders at
/// <c>InteractiveServerRenderMode(prerender: false)</c>, so an HTTP response carries the shell and
/// none of the grid. The decision under test lives entirely inside the circuit.
/// </para>
/// <para>
/// <b>This is the SECOND line, and the tests say so.</b> The rule that decides is
/// <c>SharedPicklistWrite</c> inside the two command handlers - proved in
/// <c>SharedPicklistWriteTests</c>, which drives them directly - because both commands are reachable
/// through Mediator whatever the grid renders. What these tests hold is that the page does not offer
/// an edit it knows will be refused, and marks the rows whose edits reach every tenant. A control
/// that looks editable and then fails on commit is a worse experience than one that does not offer,
/// but it is not a security hole; the reverse would be.
/// </para>
/// <para>
/// <b>Narrowed, not emptied.</b> Every "the non-holder is not offered it" assertion is paired with
/// the holder being offered it, and with the non-holder still being offered it on their OWN row.
/// A page that rendered everything read-only would satisfy the negatives alone.
/// </para>
/// </remarks>
[TestFixture]
public class SharedPicklistGridComponentTests
{
    private const string TenantA = "tenant-a";

    private BunitContext _ctx = null!;

    [TearDown]
    public async Task TearDown() => await _ctx.DisposeAsync();

    /// <summary>One shared row and one of the caller's own, so both cases are on screen at once.</summary>
    private static readonly PicklistSetDto SharedRow = new()
    {
        Id = 1, Name = Picklist.Status, Value = "shipped-value", Text = "Shipped", TenantId = null
    };

    private static readonly PicklistSetDto OwnRow = new()
    {
        Id = 2, Name = Picklist.Brand, Value = "own-value", Text = "Own", TenantId = TenantA
    };

    private void Arrange(bool canManageShared)
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
        services.AddSingleton(Mock.Of<IAppCache>());
        services.AddSingleton(Mock.Of<IObjectMapper>());
        // Concrete, not a mock: the type is sealed-by-convention with a JSRuntime dependency, and
        // bUnit's loose JSInterop satisfies it. The page injects it for the export button only.
        services.AddScoped<BlazorDownloadFileService>();

        var profileState = new Mock<IUserProfileState>();
        profileState.SetupGet(x => x.Value).Returns(UserProfile.Empty);
        services.AddSingleton(profileState.Object);

        var mediator = new Mock<IMediator>();
        mediator.Setup(x => x.Send(It.IsAny<PicklistSetsWithPaginationQuery>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<PaginatedData<PicklistSetDto>>(
                new PaginatedData<PicklistSetDto>(new List<PicklistSetDto> { SharedRow, OwnRow }, 2, 1, 10)));
        services.AddSingleton(mediator.Object);

        // The one thing under test. Everything else is granted, so a missing affordance below is
        // attributable to ManageShared and to nothing else.
        var permissions = new Mock<IPermissionService>();
        permissions.Setup(x => x.GetAccessRightsAsync<PicklistSetsAccessRights>())
            .ReturnsAsync(new PicklistSetsAccessRights
            {
                View = true, Create = true, Edit = true, Delete = true,
                Search = true, Export = true, Import = true,
                ManageShared = canManageShared
            });
        services.AddSingleton(permissions.Object);
    }

    private IRenderedComponent<Server.UI.Pages.PicklistSets.PicklistSets> Render() =>
        _ctx.Render<Server.UI.Pages.PicklistSets.PicklistSets>();

    /// <summary>The chip the grid renders beside a shared row's name.</summary>
    private const string SharedMarker = "Shared";

    // ---- the marker -----------------------------------------------------------------------------

    [Test]
    public void ASharedRowIsMarkedInTheGrid()
    {
        // Pass 31 A5: the page could not tell a shared row from a private one, because
        // PicklistSetDto carried no TenantId. Adding it is what made this renderable at all.
        Arrange(canManageShared: true);

        var markup = Render().Markup;

        markup.Should().Contain(SharedMarker,
            "an administrator editing a value that reaches every tenant should be able to see that " +
            "that is what they are doing");
    }

    [Test]
    public void AMarkerIsShownWhetherOrNotThePrincipalMayChangeTheRow()
    {
        // The mark is information, not an affordance. A principal who cannot edit shared rows still
        // needs to know why one of them is read-only.
        Arrange(canManageShared: false);

        Render().Markup.Should().Contain(SharedMarker);
    }

    // ---- the delete affordance -------------------------------------------------------------------

    [Test]
    public void WithoutManageShared_TheDeleteButtonOnASharedRowIsDisabled()
    {
        // The per-row Disabled expression, which is the one affordance that is directly observable
        // in markup: MudBlazor renders `disabled` on the button element.
        Arrange(canManageShared: false);

        var buttons = Render().FindAll("button[aria-label]")
            .Where(b => b.GetAttribute("aria-label")?.Contains("Delete", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        buttons.Should().NotBeEmpty("the grid renders a delete button per row");

        buttons.Count(b => b.HasAttribute("disabled")).Should().Be(1,
            "exactly the shared row's delete is disabled - the caller's own row keeps its button, " +
            "which is the narrowed-not-emptied half");
    }

    [Test]
    public void WithManageShared_NoDeleteButtonIsDisabled()
    {
        // The holder's control. Same markup, same rows, one permission different.
        Arrange(canManageShared: true);

        var buttons = Render().FindAll("button[aria-label]")
            .Where(b => b.GetAttribute("aria-label")?.Contains("Delete", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        buttons.Should().NotBeEmpty();
        buttons.Should().NotContain(b => b.HasAttribute("disabled"),
            "a holder may delete both the shared row and their own");
    }

    // ---- the rule the editors read ---------------------------------------------------------------

    [Test]
    public void TheGridsWriteTestIsTheSameShapeAsTheHandlersRule()
    {
        // The cell EDITORS branch on CanWrite, and a cell only enters edit mode under a real click,
        // which bUnit's static render does not reproduce. So the editors themselves are covered by
        // the hand-check below rather than asserted here; what IS asserted is that the page's rule
        // and the handler's rule agree about what "shared" means, which is the part that could
        // silently diverge.
        SharedPicklistWrite.IsShared(SharedRow.TenantId).Should().BeTrue();
        SharedPicklistWrite.IsShared(OwnRow.TenantId).Should().BeFalse();

        SharedRow.IsShared.Should().BeTrue(
            "PicklistSetDto.IsShared delegates to SharedPicklistWrite rather than restating the rule");
        OwnRow.IsShared.Should().BeFalse();
    }
}
