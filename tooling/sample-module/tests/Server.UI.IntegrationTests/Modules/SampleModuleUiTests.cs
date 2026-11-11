#nullable enable
using System.Linq;
using System.Reflection;
using CleanArchitecture.Blazor.Application.Common.Modules;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Server.UI.AppLevel.Pages;
using CleanArchitecture.Blazor.Server.UI.Services.Navigation;
using CleanArchitecture.Blazor.TestSupport;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests.Modules;

/// <summary>Pass 60b plan §5 rows S7 and S9: the sample module's menu and the application-level page, on the real host.</summary>
[TestFixture]
public class SampleModuleUiTests
{
    [Test]
    public void S7_TheSampleSection_IsBetweenApplicationAndManagement_OnTheRealHost()
    {
        using var factory = new GxWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var titles = scope.ServiceProvider.GetRequiredService<IMenuService>().Features.Select(s => s.Title).ToList();

        titles.IndexOf("SAMPLE").Should().BeGreaterThan(titles.IndexOf("Application")).And.BeLessThan(titles.IndexOf("MANAGEMENT"));
        factory.Services.GetRequiredService<ModuleServiceLedger>().Owners.Should().Contain("Sample").And.Contain("AppLevel");
    }

    [Test]
    public void S9_TheApplicationLevelPage_IsRouted_Gated_AndKeepsTheBoundary()
    {
        var page = typeof(SampleTools);
        page.GetCustomAttribute<RouteAttribute>()!.Template.Should().Be("/sample-tools");
        page.GetCustomAttribute<AuthorizeAttribute>()!.Policy.Should().Be(Permissions.SampleTools.Use);
        ModuleBoundaryRule.IsAppLevel(page).Should().BeTrue();

        var result = ModuleBoundaryRule.Check(page);
        result.Violations.Should().BeEmpty("the page sends the module's command, which the application level may");
        TypeReferenceScanner.Scan(page).References.Should().Contain(r => r.To.Name == "CreateSampleBinCommand");
    }
}
