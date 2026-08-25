#nullable enable
using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;
using CleanArchitecture.Blazor.Server.UI.Components.Errors;
using CleanArchitecture.Blazor.Server.UI.Services;
using CleanArchitecture.Blazor.Server.UI.Services.Layout;
using FluentAssertions;
using Mapster;
using Mediator;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Components;

/// <summary>
/// Regression tests for the environment gate on <see cref="CustomError"/>. The component is mounted
/// globally by AppLayout's ErrorBoundary, and before the fix it unwrapped to the innermost exception
/// and offered its message and stack trace behind a UI toggle in every environment, production
/// included.
/// </summary>
[TestFixture]
public class CustomErrorEnvironmentGatingTests
{
    private const string SecretMessage = "connection string password=hunter2 leaked here";

    private static ServiceProvider BuildServices(string environmentName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMudServices();
        services.AddAuthorizationCore();

        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(x => x.EnvironmentName).Returns(environmentName);
        environment.SetupGet(x => x.ApplicationName).Returns("Server.UI");
        services.AddSingleton(environment.Object);

        // Every component inherits the @inject directives declared in Server.UI/_Imports.razor, so all
        // of them must be resolvable for the component to be instantiated at all.
        services.AddSingleton<NavigationManager, TestNavigationManager>();
        services.AddSingleton(Mock.Of<IUserProfileState>());
        services.AddSingleton(Mock.Of<IApplicationSettings>());
        services.AddSingleton(Mock.Of<IValidationService>());
        services.AddSingleton(Mock.Of<IJSRuntime>());
        services.AddSingleton(Mock.Of<IMediator>());
        services.AddSingleton(Mock.Of<IAppCache>());
        services.AddSingleton(Mock.Of<IPermissionService>());
        services.AddSingleton(Mock.Of<IObjectMapper>());
        services.AddSingleton(new TypeAdapterConfig());
        services.AddSingleton<DialogServiceHelper>();
        services.AddSingleton(typeof(IStringLocalizer<>), typeof(PassThroughLocalizer<>));

        return services.BuildServiceProvider();
    }

    private static async Task<string> RenderAsync(string environmentName)
    {
        await using var provider = BuildServices(environmentName);
        var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
        await using var renderer = new HtmlRenderer(provider, loggerFactory);

        // The component reads a cascading AuthenticationState, so it is rendered inside a
        // CascadingValue built directly with a RenderTreeBuilder.
        var exception = new InvalidOperationException(SecretMessage);
        try
        {
            throw exception;
        }
        catch (InvalidOperationException)
        {
            // Populates StackTrace, which is what the gate must hide outside Development.
        }

        var child = (RenderFragment)(builder =>
        {
            builder.OpenComponent<CustomError>(0);
            builder.AddComponentParameter(1, nameof(CustomError.Exception), exception);
            builder.CloseComponent();
        });

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "tester") }, "TestAuth"));

        var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            ["Value"] = Task.FromResult(new AuthenticationState(principal)),
            ["ChildContent"] = child
        });

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<CascadingValue<Task<AuthenticationState>>>(parameters);
            return output.ToHtmlString();
        });
    }

    [Test]
    public async Task InProduction_NeitherTheDetailsToggleNorTheExceptionMessageIsRendered()
    {
        var html = await RenderAsync("Production");

        html.Should().NotContain(SecretMessage, "the raw exception message must never reach a production user");
        html.Should().NotContain("Show Technical Details", "the toggle itself is gated too");
        html.Should().NotContain("Stack Trace");
    }

    [Test]
    public async Task InDevelopment_TheDetailsToggleIsRendered()
    {
        var html = await RenderAsync("Development");

        html.Should().Contain("Show Technical Details");
    }

    [Test]
    public async Task TheFriendlyMessageIsShownInEveryEnvironment()
    {
        // GetUserFriendlyMessage maps InvalidOperationException to this string; it is not gated.
        const string friendly = "The current operation is invalid in this context.";

        (await RenderAsync("Production")).Should().Contain(friendly);
        (await RenderAsync("Development")).Should().Contain(friendly);
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("https://localhost/", "https://localhost/boom");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }

    /// <summary>Localizer that returns the key unchanged, so assertions can match on literal text.</summary>
    private sealed class PassThroughLocalizer<T> : IStringLocalizer<T>
    {
        public LocalizedString this[string name] => new(name, name, resourceNotFound: false);
        public LocalizedString this[string name, params object[] arguments] =>
            new(name, string.Format(name, arguments), resourceNotFound: false);
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) =>
            Array.Empty<LocalizedString>();
    }
}
#nullable restore
