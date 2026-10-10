using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure;
using CleanArchitecture.Blazor.Application.Common.Extensions;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Application.Common.PublishStrategies;
using CleanArchitecture.Blazor.Infrastructure.Services.Identity;
using CleanArchitecture.Blazor.TestSupport;
using Mediator;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moq;
using NUnit.Framework;
using CleanArchitecture.Blazor.Application.Features.PicklistSets.DTOs;
using CleanArchitecture.Blazor.Infrastructure.Services;
using CleanArchitecture.Blazor.Application.Features.Tenants.DTOs;
using CleanArchitecture.Blazor.Application.Common.Interfaces.MultiTenant;
using CleanArchitecture.Blazor.Application.IntegrationTests.MultiTenant;

namespace CleanArchitecture.Blazor.Application.IntegrationTests;

[SetUpFixture]
public class Testing
{
    private static IConfigurationRoot _configuration = null!;
    private static IServiceScopeFactory _scopeFactory = null!;
    private static string? _currentUserId;

    /// <summary>
    /// True while the harness is loading the current user's context. The loader queries the database,
    /// and a query that read the ambient user would call back into <c>Current</c> and recurse until the
    /// stack overflowed, crashing the test host rather than failing a test. While it loads, the ambient
    /// user is nobody, which is what the application's own accessor holds at that point.
    /// </summary>
    private static readonly AsyncLocal<bool> LoadingContext = new();

    /// <summary>This assembly's own database on the GX_TEST_PG server. No other assembly uses it.</summary>
    public const string DatabaseName = TestDatabaseNames.AppInt;

    /// <summary>The shared test database: migrated once per run, emptied before every test.</summary>
    public static PostgresTestDatabase Database { get; private set; } = null!;

    /// <summary>The application's clock. <see cref="TestClock.Set"/> fixes it; <see cref="ResetState"/> frees it.</summary>
    public static TestClock Clock { get; } = new();

    [OneTimeSetUp]
    public async Task RunBeforeAnyTests()
    {
        // No default server, and no skipping. A run that reports these tests as skipped looks green
        // in a summary while proving nothing, so a missing or malformed GX_TEST_PG, or a database name
        // TestDatabaseGuard refuses, FAILS every test with the reason. Creating the database, the
        // stale-history check (with its manual drop command) and the migration all happen in
        // EnsureReadyAsync; see PostgresTestDatabase.
        Database = PostgresTestDatabase.FromEnvironment(DatabaseName);
        await Database.EnsureReadyAsync();

        _configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", true, true)
            .AddEnvironmentVariables()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DatabaseSettings:DBProvider"] = DbProviderKeys.Npgsql,
                ["DatabaseSettings:ConnectionString"] = Database.ConnectionString,
                // No log database: this suite asserts on handlers, not on log rows, and the
                // application runs normally without one (console and file logging only).
                ["DatabaseSettings:LogConnectionString"] = string.Empty
            })
            .Build();

        _scopeFactory = CreateServices().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    /// <summary>The harness's registrations: the application's own, with the ambient user and the clock replaced.</summary>
    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton(Mock.Of<IWebHostEnvironment>(w =>
            w.EnvironmentName == "Development" &&
            w.ApplicationName == "Server.UI"));
        // Options post-configuration reads IHostEnvironment (MailSettings today), as a real host provides it.
        services.AddSingleton<IHostEnvironment>(p => p.GetRequiredService<IWebHostEnvironment>());

        services.AddInfrastructure(_configuration)
            .AddApplication();

        services.AddMediator(options =>
        {
            options.Assemblies = [typeof(CleanArchitecture.Blazor.Application.DependencyInjection)];
            options.NotificationPublisherType = typeof(ChannelBasedNoWaitPublisher);
            options.ServiceLifetime = ServiceLifetime.Scoped;
        });

        // The ambient user. Evaluated per call, not once at registration: this is a singleton, and
        // _currentUserId is set by RunAsUserAsync after the container has been built.
        //
        // Loaded through the application's own IUserContextLoader, cache included, rather than built
        // here (CO-162). The tenants a user may see, and the roles, are computed by the loader from the
        // user's row and membership rows, and a membership change has to clear the loader's cache to
        // show. A hand-built context carried only an id and a name, so every test of those rules would
        // have been testing this file instead of the application.
        //
        // PUSHES ARE HONOURED (Pass 54). This was a Moq double whose Push did nothing, which was
        // harmless while nothing in the application pushed outside a hub. ISystemContext and the
        // tenant seed runner now do - the first pushes the system account, the second pushes "no
        // principal" - and a harness that ignored both would report the harness user throughout,
        // so the tests of those two would be testing this file again. A pushed context, including a
        // pushed null, wins; with nothing pushed the harness user is the ambient one, as before.
        services.RemoveAll<IUserContextAccessor>();
        services.AddSingleton<IUserContextAccessor>(provider =>
            new HarnessUserContextAccessor(provider.GetRequiredService<IUserContextLoader>()));

        // The clock, settable (CO-165). Scoped, as the application registers IDateTime, over one instance.
        // The one test tenant seeder (Pass 54), inert unless a test enables it - see
        // RecordingTenantSeeder. Registered the way a project registers its own.
        services.AddScoped<ITenantSeeder, RecordingTenantSeeder>();

        services.RemoveAll<IDateTime>();
        services.AddScoped<IDateTime>(_ => Clock);
        return services;
    }

    private static UserContext? CurrentContext(IUserContextLoader loader)
    {
        if (string.IsNullOrEmpty(_currentUserId) || LoadingContext.Value) return null;

        LoadingContext.Value = true;
        try
        {
            return loader.LoadAsync(PrincipalFor(_currentUserId)).GetAwaiter().GetResult();
        }
        finally
        {
            LoadingContext.Value = false;
        }
    }

    /// <summary>A signed-in principal for <paramref name="userId"/>, as the cookie would produce.</summary>
    /// <summary>The harness user, under a stack of explicit pushes that take precedence over it.</summary>
    private sealed class HarnessUserContextAccessor : IUserContextAccessor
    {
        private sealed class Node
        {
            public UserContext? Value;
            public Node? Parent;
        }

        private static readonly AsyncLocal<Node?> Pushed = new();
        private readonly IUserContextLoader _loader;

        public HarnessUserContextAccessor(IUserContextLoader loader) => _loader = loader;

        public UserContext? Current => Pushed.Value is { } node ? node.Value : CurrentContext(_loader);

        public IDisposable Push(UserContext? context)
        {
            var parent = Pushed.Value;
            Pushed.Value = new Node { Value = context, Parent = parent };
            return new Pop(parent);
        }

        public void Clear() => Pushed.Value = null;

        private sealed class Pop(Node? restore) : IDisposable
        {
            public void Dispose() => Pushed.Value = restore;
        }
    }

    private static ClaimsPrincipal PrincipalFor(string userId) =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "harness"));

    /// <summary>
    /// Acts as an existing user from now on, or as nobody with <c>null</c>, without creating one and
    /// without touching their cached context, exactly as a user who has not signed out.
    /// </summary>
    public static void UseUser(string? userId) => _currentUserId = userId;

    public static async Task<TResponse> SendAsync<TResponse>(IRequest<TResponse> request)
    {
        using var scope = _scopeFactory.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        return await mediator.Send(request);
    }

    public static async Task<string> RunAsDefaultUserAsync()
    {
        return await RunAsUserAsync("TestUser", "Password123!", new string[] { });
    }

    public static async Task<string> RunAsAdministratorAsync()
    {
        return await RunAsUserAsync("administrator", "Password123!", new[] { "Admin" });
    }

    public static async Task<string> RunAsUserAsync(string userName, string password, string[] roles)
    {
        using var scope = _scopeFactory.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        // Email = userName produced a bare name, which Identity's EmailValidator rejects - this helper
        // could never succeed, which is why no test used it before deny-by-default required one.
        var user = new ApplicationUser { UserName = userName, Email = $"{userName}@example.com" };
        var result = await userManager.CreateAsync(user, password);

        if (roles.Any())
        {
            // ApplicationRole, not IdentityRole. The application registers
            // .AddRoles<ApplicationRole>(), so RoleManager<IdentityRole> was never in the
            // container: GetService returned null and the next line threw
            // NullReferenceException, which made this helper unusable. Nothing caught it
            // because nothing called it - RunAsDefaultUserAsync passes an empty roles array,
            // so the null was never dereferenced. Pass 28 A9; catalogue defect #15.
            //
            // GetRequiredService, not GetService: a missing registration must fail saying
            // which service is missing, rather than null-referencing one line later. That
            // substitution is what hid this defect, so it is corrected here too.
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            foreach (var role in roles)
            {
                await roleManager.CreateAsync(new ApplicationRole(role));
            }
            await userManager.AddToRolesAsync(user, roles);
        }

        if (result.Succeeded)
        {
            // The application layer now denies any request whose permission the principal does not
            // hold - see AuthorizationBehaviour. These tests exercise handlers, not authorization,
            // so the harness user is granted the full permission set exactly as the seeded Admin
            // role is. The authorization rules themselves are covered by AuthorizationBehaviourTests
            // and RequestAuthorizationRegistryTests.
            await GrantAllPermissionsAsync(userManager, user);
            _currentUserId = user.Id;
            return _currentUserId;
        }

        var errors = string.Join(Environment.NewLine, result.ToApplicationResult().Errors);
        throw new Exception($"Unable to create {userName}.{Environment.NewLine}{errors}");
    }

    /// <summary>
    /// Grants every permission constant to the user, mirroring the reflection grant the seeder
    /// applies to the Admin role (ApplicationDbContextInitializer.SeedRolesAsync).
    /// </summary>
    private static async Task GrantAllPermissionsAsync(UserManager<ApplicationUser> userManager, ApplicationUser user)
    {
        var permissions = typeof(Permissions).GetNestedTypes()
            .SelectMany(module => module.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
            .Select(field => field.GetValue(null) as string)
            .Where(value => !string.IsNullOrEmpty(value))
            .Distinct();

        foreach (var permission in permissions)
        {
            await userManager.AddClaimAsync(user, new Claim(ApplicationClaimTypes.Permission, permission!));
        }
    }

    public static async Task ResetState()
    {
        await Database.ResetAsync();
        Clock.Reset();
        RecordingTenantSeeder.Reset();
        _currentUserId = null;

        // Re-establish an authenticated principal after the wipe: with deny-by-default in the
        // pipeline, a test that dispatches anything needs an ambient user context to authorize.
        await RunAsDefaultUserAsync();
    }

    public static async Task<TEntity?> FindAsync<TEntity>(params object[] keyValues)
        where TEntity : class
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await context.FindAsync<TEntity>(keyValues);
    }

    /// <summary>
    /// A service scope over the harness container, for tests needing a service the
    /// purpose-built helpers do not expose.
    /// </summary>
    /// <remarks>
    /// Added by Pass 29 so <c>HarnessPrincipalTests</c> can verify the role helpers through
    /// UserManager and RoleManager. Pass 28 reached the private scope factory by reflection
    /// rather than modify this file for a scratch probe; a permanent test earns a real seam.
    /// </remarks>
    public static IServiceScope CreateScope() => _scopeFactory.CreateScope();
    public static IApplicationDbContext CreateDbContext()
    {
        var scope = _scopeFactory.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    }

    public static async Task AddAsync<TEntity>(TEntity entity)
        where TEntity : class
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        context.Add(entity);
        await context.SaveChangesAsync();
    }

    public static async Task<int> CountAsync<TEntity>() where TEntity : class
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await context.Set<TEntity>().CountAsync();
    }

    public static IDataSourceService<PicklistSetDto> CreatePicklistService()
    {
        var scope = _scopeFactory.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IDataSourceService<PicklistSetDto>>();
    }

    public static IDataSourceService<TenantDto> CreateTenantsService()
    {
        var scope = _scopeFactory.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IDataSourceService<TenantDto>>();
    }
}
