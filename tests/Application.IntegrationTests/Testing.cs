using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
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
using Mediator;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Respawn;
using Respawn.Graph;
using CleanArchitecture.Blazor.Application.Features.PicklistSets.DTOs;
using CleanArchitecture.Blazor.Infrastructure.Services;
using CleanArchitecture.Blazor.Application.Features.Tenants.DTOs;
using CleanArchitecture.Blazor.Infrastructure.Services.MultiTenant;
using System.Data.Common;

namespace CleanArchitecture.Blazor.Application.IntegrationTests;

[SetUpFixture]
public class Testing
{
    private static IConfigurationRoot _configuration;
    private static IServiceScopeFactory _scopeFactory;
    private static Respawner _checkpoint;
    private static string _currentUserId;

    [OneTimeSetUp]
    public async Task RunBeforeAnyTests()
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", true, true)
            .AddEnvironmentVariables();

        _configuration = builder.Build();

        //var startup = new Startup(_configuration);

        var services = new ServiceCollection();

        services.AddSingleton(Mock.Of<IWebHostEnvironment>(w =>
            w.EnvironmentName == "Development" &&
            w.ApplicationName == "Server.UI"));

        services.AddInfrastructure(_configuration)
            .AddApplication();

        services.AddMediator(options =>
        {
            options.Assemblies = [typeof(CleanArchitecture.Blazor.Application.DependencyInjection)];
            options.NotificationPublisherType = typeof(ChannelBasedNoWaitPublisher);
            options.ServiceLifetime = ServiceLifetime.Scoped;
        });

        //services.AddLogging();

        //startup.ConfigureServices(services);

        // 替换 IUserContextAccessor 的注册
        var userContextServiceDescriptor = services.FirstOrDefault(d =>
            d.ServiceType == typeof(IUserContextAccessor));
        if (userContextServiceDescriptor != null)
        {
            services.Remove(userContextServiceDescriptor);
        }

        // 使用 Moq 创建 Mock 对象并配置 Current 属性
        services.AddSingleton<IUserContextAccessor>(provider =>
        {
            var mockUserContextAccessor = new Mock<IUserContextAccessor>();
            // Evaluated per call, not once at registration: this is a singleton, and _currentUserId
            // is set by RunAsUserAsync after the container has already been built. Capturing it
            // eagerly left Current null forever, which deny-by-default now turns into a denial.
            mockUserContextAccessor.Setup(x => x.Current).Returns(() =>
                string.IsNullOrEmpty(_currentUserId)
                    ? null
                    : new UserContext(
                        UserId: _currentUserId,
                        UserName: "admin",
                        DisplayName: null,
                        Email: "admin@example.com"));
            return mockUserContextAccessor.Object;
        });

        _scopeFactory = services.BuildServiceProvider().GetService<IServiceScopeFactory>();
        EnsureDatabase();
        using var scope = services.BuildServiceProvider().CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            _checkpoint = await Respawner.CreateAsync(
                connection,
                new RespawnerOptions
                {
                    TablesToIgnore = new Table[] { "__EFMigrationsHistory" }
                });
        }
        finally
        {
            await connection.CloseAsync();
        }

        
    }

    /// <summary>
    /// Brings the test database up to the current migrations, recreating it if its history no longer
    /// matches them.
    /// </summary>
    /// <remarks>
    /// This database persists between runs on a developer machine, so it outlives the migrations it
    /// was built from. Regenerating <c>InitialCreate</c> - which Pass 7-2 and Pass 11B both did, and
    /// which is the established way to change the business schema here - leaves a database whose
    /// <c>__EFMigrationsHistory</c> names a migration that no longer exists. <c>Migrate()</c> then
    /// tries to apply the new one from scratch and fails with "There is already an object named
    /// 'AspNetRoles'", nine times, until somebody drops the database by hand. It cost exactly that in
    /// Pass 11B and was recorded as an anomaly rather than fixed.
    /// <para>
    /// The fix compares what the database has applied against what the assembly defines, and starts
    /// over only when they disagree. That is deliberately narrower than deleting unconditionally:
    /// <c>EnsureDeleted</c> on every run would cost a full schema rebuild plus reseed each time the
    /// suite starts, for a problem that occurs only when migrations are regenerated. As written, the
    /// normal path is one extra metadata query - a few milliseconds - and the expensive path happens
    /// exactly when it is the only thing that works.
    /// </para>
    /// </remarks>
    private static void EnsureDatabase()
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetService<ApplicationDbContext>();

        if (HasStaleMigrationHistory(context))
        {
            // Not a silent recovery: a developer who has just regenerated migrations should be told
            // why their test database vanished, rather than wondering where their seed data went.
            Console.WriteLine(
                "Integration test database has a migration history that no longer matches this " +
                "assembly's migrations - recreating it. This is expected after regenerating InitialCreate.");

            context.Database.EnsureDeleted();
        }

        context.Database.Migrate();
    }

    /// <summary>
    /// Whether the database claims migrations this assembly no longer defines.
    /// </summary>
    /// <remarks>
    /// Only that direction is a problem. A database MISSING migrations the assembly defines is the
    /// ordinary pending-migration case, and <c>Migrate()</c> handles it correctly.
    /// </remarks>
    private static bool HasStaleMigrationHistory(ApplicationDbContext context)
    {
        try
        {
            if (!context.Database.GetService<IRelationalDatabaseCreator>().Exists()) return false;

            var applied = context.Database.GetAppliedMigrations().ToHashSet(StringComparer.Ordinal);
            if (applied.Count == 0) return false;

            var defined = context.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
            return applied.Except(defined).Any();
        }
        catch (Exception)
        {
            // An unreachable or half-built database is not something to interpret here; let
            // Migrate() fail with its own, better, message.
            return false;
        }
    }

    public static async Task<TResponse> SendAsync<TResponse>(IRequest<TResponse> request)
    {
        using var scope = _scopeFactory.CreateScope();
        var mediator = scope.ServiceProvider.GetService<IMediator>();
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
        var userManager = scope.ServiceProvider.GetService<UserManager<ApplicationUser>>();
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
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            await _checkpoint.ResetAsync(connection);
        }
        finally
        {
            await connection.CloseAsync();
        }
        _currentUserId = null;

        // Re-establish an authenticated principal after the wipe: with deny-by-default in the
        // pipeline, a test that dispatches anything needs an ambient user context to authorize.
        await RunAsDefaultUserAsync();
    }

    public static async Task<TEntity> FindAsync<TEntity>(params object[] keyValues)
        where TEntity : class
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetService<ApplicationDbContext>();
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
        var context = scope.ServiceProvider.GetService<ApplicationDbContext>();
        context.Add(entity);
        await context.SaveChangesAsync();
    }

    public static async Task<int> CountAsync<TEntity>() where TEntity : class
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetService<ApplicationDbContext>();
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

    [OneTimeTearDown]
    public void RunAfterAnyTests()
    {
    }
}
