using System.Net.Http.Headers;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.PublishStrategies;
using CleanArchitecture.Blazor.Infrastructure.Services.Identity;
using CleanArchitecture.Blazor.Server.UI.Endpoints;
using CleanArchitecture.Blazor.Server.UI.Extensions;
using CleanArchitecture.Blazor.Server.UI.Hubs;
using CleanArchitecture.Blazor.Server.UI.Middlewares;
using CleanArchitecture.Blazor.Server.UI.Services;
using CleanArchitecture.Blazor.Server.UI.Services.JsInterop;
using CleanArchitecture.Blazor.Server.UI.Services.Layout;
using CleanArchitecture.Blazor.Server.UI.Services.Navigation;
using CleanArchitecture.Blazor.Server.UI.Services.Notifications;
using CleanArchitecture.Blazor.Server.UI.Services.UserPreferences;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.FileProviders;
using MudBlazor.Services;
using QuestPDF;
using QuestPDF.Infrastructure;



namespace CleanArchitecture.Blazor.Server.UI;

/// <summary>
/// Provides dependency injection configuration for the server UI.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Route prefix of the Blazor circuit endpoints (negotiate, the hub itself, disconnect,
    /// initializers) that AddInteractiveServerRenderMode maps alongside the page endpoints.
    /// </summary>
    private const string BlazorCircuitPathPrefix = "/_blazor";

    /// <summary>
    /// Adds server UI services to the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="config">The configuration.</param>
    /// <returns>The updated service collection.</returns>
    public static IServiceCollection AddServerUI(this IServiceCollection services, IConfiguration config)
    {
        services.AddRazorComponents().AddInteractiveServerComponents().AddHubOptions(options => options.MaximumReceiveMessageSize = 10 * 1024 * 1024);
        services.AddCascadingAuthenticationState();
        services.AddMudServices(config =>
        {
      
            config.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomCenter;
            config.SnackbarConfiguration.NewestOnTop = false;
            config.SnackbarConfiguration.ShowCloseIcon = true;
            config.SnackbarConfiguration.VisibleStateDuration = 3000;
            config.SnackbarConfiguration.HideTransitionDuration = 500;
            config.SnackbarConfiguration.ShowTransitionDuration = 500;
            config.SnackbarConfiguration.SnackbarVariant = Variant.Filled;
            config.SnackbarConfiguration.PreventDuplicates = false;

        });
        services.AddMudPopoverService();
        services.AddMudBlazorSnackbar();
        services.AddMudBlazorDialog();

        //services.AddDataProtectionKeyCheck();

        services.AddScoped<LocalizationCookiesMiddleware>()
            .Configure<RequestLocalizationOptions>(options =>
            {
    
                options.AddSupportedUICultures(LocalizationConstants.SupportedLanguages.Select(x => x.Code).ToArray());
                options.AddSupportedCultures(LocalizationConstants.SupportedLanguages.Select(x => x.Code).ToArray());
                options.DefaultRequestCulture = new RequestCulture(LocalizationConstants.DefaultLanguageCode);
                options.FallBackToParentUICultures = true;
            })
            .AddLocalization(options => options.ResourcesPath = LocalizationConstants.ResourcesPath);

        services.AddHangfire(configuration => configuration
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_170)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                .UseInMemoryStorage())
            .AddHangfireServer()
            .AddMvc();

        services.AddControllers();

        services.AddSignalR(options =>
            {
                options.MaximumReceiveMessageSize = 10 * 1024 * 1024;
                options.AddFilter<UserContextHubFilter>();
            });
        
      
        
        services.AddExceptionHandler<GlobalExceptionHandler>();
        services.AddProblemDetails();
        services.AddHealthChecks();

        services.AddScoped<LocalTimeOffset>();
        services.AddScoped<IHubConnectionFactory, HubConnectionFactory>()
            .AddScoped<HubClient>();
        services
            .AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>()
            .AddScoped<LayoutService>()
            .AddScoped<DialogServiceHelper>()
            .AddScoped<BlazorDownloadFileService>()
            .AddScoped<IUserPreferencesService, UserPreferencesService>()
            .AddScoped<IMenuService, MenuService>()
            .AddScoped<InMemoryNotificationService>()
            .AddScoped<INotificationService>(sp =>
            {
                var service = sp.GetRequiredService<InMemoryNotificationService>();
                service.Preload();
                return service;
            });


        return services;
    }

    /// <summary>
    /// Configures the server pipeline.
    /// </summary>
    /// <param name="app">The web application.</param>
    /// <param name="config">The configuration.</param>
    /// <returns>The configured web application.</returns>
    public static WebApplication ConfigureServer(this WebApplication app, IConfiguration config)
    {
        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.UseMigrationsEndPoint();
        }
        else
        {
            // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
            app.UseHsts();
        }

        // Single global exception handler registration to activate IExceptionHandler (GlobalExceptionHandler) + ProblemDetails pipeline.
        app.UseExceptionHandler();
        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
        app.UseForwardedHeaders();
        // Liveness must answer before a user has authenticated, so it opts out of the fallback policy.
        app.MapHealthChecks("/health").AllowAnonymous();
        //app.UseDataProtectionKeyCheck();
        app.UseAuthentication();
        app.UseAuthorization();
        // After authentication, so context.User carries the MustChangePassword claim; before the
        // endpoints, so a flagged user cannot reach one. Only the HTTP half - in-circuit navigation
        // is guarded by ForcePasswordChangeGuard inside AppLayout.
        app.UseMiddleware<ForcePasswordChangeMiddleware>();
        // Before the endpoints, so a disabled registration surface is unreachable by direct URL as
        // well as by the (hidden) link on the login page.
        app.UseMiddleware<SelfRegistrationMiddleware>();
        app.UseAntiforgery();
        app.UseHttpsRedirection();
        // Framework and static assets must load for the anonymous login page - without this the
        // fallback policy would block blazor.web.js and no circuit could ever start.
        app.MapStaticAssets().AllowAnonymous();
        

        // The /Files PhysicalFileProvider mount that stood here is GONE, and deliberately.
        // UseStaticFiles runs before UseAuthorization, so it served every uploaded document and
        // every avatar to any anonymous caller who could guess a path - the fallback policy never
        // saw those requests. Stored files are now served by the authenticated /files endpoint
        // (FileEndpoints below), and the storage root is created on demand by the disk provider,
        // so the startup CreateDirectory call went with it.

        var localizationOptions = new RequestLocalizationOptions()
            .SetDefaultCulture(LocalizationConstants.DefaultLanguageCode)
            .AddSupportedCultures(LocalizationConstants.SupportedLanguages.Select(x => x.Code).ToArray())
            .AddSupportedUICultures(LocalizationConstants.SupportedLanguages.Select(x => x.Code).ToArray());

        // Remove AcceptLanguageHeaderRequestCultureProvider to prevent the browser's Accept-Language header from taking effect
        var acceptLanguageProvider = localizationOptions.RequestCultureProviders
            .OfType<AcceptLanguageHeaderRequestCultureProvider>()
            .FirstOrDefault();
        if (acceptLanguageProvider != null)
        {
            localizationOptions.RequestCultureProviders.Remove(acceptLanguageProvider);
        }
        app.UseRequestLocalization(localizationOptions);
        app.UseMiddleware<LocalizationCookiesMiddleware>();
        app.UseHangfireDashboard("/jobs", new DashboardOptions
        {
            Authorization = new[] { new HangfireDashboardAuthorizationFilter() },
            AsyncAuthorization = new[] { new HangfireDashboardAsyncAuthorizationFilter() }
        });
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode()
            // MapRazorComponents maps two different kinds of endpoint: one per routable page, and
            // the Blazor circuit endpoints under /_blazor that AddInteractiveServerRenderMode adds.
            // The login page is interactive, so an anonymous visitor has to be able to negotiate a
            // circuit - but exempting the whole builder would also exempt every page and silently
            // undo both the fallback policy and the [Authorize] attributes on the protected pages.
            // This convention inspects each endpoint's route pattern and exempts only the framework
            // circuit endpoints, leaving page endpoints under the policy.
            .Add(builder =>
            {
                if (builder is RouteEndpointBuilder route &&
                    route.RoutePattern.RawText?.StartsWith(BlazorCircuitPathPrefix, StringComparison.OrdinalIgnoreCase) == true)
                {
                    builder.Metadata.Add(new AllowAnonymousAttribute());
                }
            });
        app.MapHub<ServerHub>(ISignalRHub.Url);

        // Stored files: authenticated, and per-object authorized for document keys.
        app.MapFileEndpoints();

        //QuestPDF License configuration
        Settings.License = LicenseType.Community;

        // Add additional endpoints required by the Identity /Account Razor components.
        app.MapAdditionalIdentityEndpoints();
        app.UseWebSockets(new WebSocketOptions()
        { // We obviously need this
            KeepAliveInterval = TimeSpan.FromSeconds(30), // Just in case
        });
       
        return app;
    }
}
