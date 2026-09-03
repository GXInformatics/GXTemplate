#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Features.Identity.DTOs;
using CleanArchitecture.Blazor.Server.UI.Components.Inputs.Autocomplete;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor.Services;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// The "superior" picker searches within one tenant, and searches nothing at all when it has not
/// been told which.
/// </summary>
/// <remarks>
/// Its predicate was
/// <c>(x.TenantId != null &amp;&amp; x.TenantId.Equals(TenantId) || TenantId == null)</c> and its one
/// call site passed no <c>TenantId</c>, so the clause was <c>|| true</c>: a live cross-tenant user
/// directory, searchable by username or email, inside the user-edit dialog. Pass 23 §3.2 found it
/// after Pass 22 had scored the component green from its source alone.
/// <para>
/// <b>Two fixes, and the default is the important one.</b> Passing the tenant at the call site
/// repairs that call site; failing closed here repairs every call site not yet written. A filter
/// whose absent-parameter behaviour is "everything" is a filter-shaped thing that defaults to the
/// leak - which is exactly how this arose.
/// </para>
/// </remarks>
[TestFixture]
public class SuperiorAutocompleteScopeComponentTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

    private BunitContext _ctx = null!;

    [SetUp]
    public void SetUp()
    {
        _ctx = new BunitContext();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddLogging();
        _ctx.Services.AddMudServices();

        var users = new[]
        {
            Dto("a-one", TenantA),
            Dto("a-two", TenantA),
            Dto("b-one", TenantB),
            Dto("orphan", null)
        };

        var source = new Mock<IDataSourceService<ApplicationUserDto>>();
        source.Setup(x => x.InitializeAsync()).Returns(Task.CompletedTask);
        source.SetupGet(x => x.DataSource).Returns(users);
        _ctx.Services.AddSingleton(source.Object);
    }

    [TearDown]
    public async Task TearDown() => await _ctx.DisposeAsync();

    private static ApplicationUserDto Dto(string name, string? tenantId) => new()
    {
        Id = name, UserName = name, Email = $"{name}@x.com", TenantId = tenantId
    };

    /// <summary>Runs the component's own search function, as MudAutocomplete would.</summary>
    private async Task<string[]> SearchAsync(string? tenantId, string? term = null)
    {
        var component = _ctx.Render<PickSuperiorAutocomplete<ApplicationUserDto>>(p => p
            .Add(x => x.TenantId, tenantId)
            .Add(x => x.OwnerName, "nobody"));

        var search = component.Instance.SearchFunc;
        search.Should().NotBeNull("the component assigns its own search function in its constructor");

        var task = search!(term!, CancellationToken.None);
        var results = task is null ? Enumerable.Empty<ApplicationUserDto>() : await task;
        return results.Select(u => u.UserName).OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    // ---- the default -------------------------------------------------------------------------------

    [Test]
    public async Task WithNoTenant_ItSearchesNothing()
    {
        // RED before Pass 27: every user in every tenant. This is the half of the fix that protects
        // call sites nobody has written yet.
        (await SearchAsync(tenantId: null)).Should().BeEmpty();
        (await SearchAsync(tenantId: string.Empty)).Should().BeEmpty();
    }

    // ---- the bound ---------------------------------------------------------------------------------

    [Test]
    public async Task WithATenant_ItSearchesOnlyThatTenant()
    {
        var found = await SearchAsync(TenantA);

        found.Should().BeEquivalentTo("a-one", "a-two");
    }

    [Test]
    public async Task ItStillFindsEveryColleagueInThatTenant()
    {
        // Narrowed, not emptied: both tenant-A users come back, and a keyword still matches within
        // the tenant rather than across it.
        (await SearchAsync(TenantA)).Should().HaveCount(2);
        (await SearchAsync(TenantA, "a-")).Should().BeEquivalentTo("a-one", "a-two");
    }

    [Test]
    public async Task AKeywordCannotReachAnotherTenant()
    {
        // The leak in its original form: the search matched username OR email across every tenant.
        (await SearchAsync(TenantA, "b-one")).Should().BeEmpty();
        (await SearchAsync(TenantA, "b-one@x.com")).Should().BeEmpty();
    }

    [Test]
    public async Task ATenantlessUserIsNeverOffered()
    {
        (await SearchAsync(TenantA)).Should().NotContain("orphan");
        (await SearchAsync(TenantB)).Should().NotContain("orphan");
    }

    [Test]
    public async Task TheOwnerIsExcludedFromTheirOwnSuperiorList()
    {
        // Pre-existing behaviour, asserted because the predicate was rewritten around it.
        var component = _ctx.Render<PickSuperiorAutocomplete<ApplicationUserDto>>(p => p
            .Add(x => x.TenantId, TenantA)
            .Add(x => x.OwnerName, "a-one"));

        var search = component.Instance.SearchFunc;
        search.Should().NotBeNull();

        var task = search!(null!, CancellationToken.None);
        var results = task is null ? Enumerable.Empty<ApplicationUserDto>() : await task;

        results.Select(u => u.UserName).Should().BeEquivalentTo("a-two");
    }
}
#nullable restore
