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

    private static void EnsureDatabase()
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetService<ApplicationDbContext>();
        context.Database.Migrate();
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
            var roleManager = scope.ServiceProvider.GetService<RoleManager<IdentityRole>>();
            foreach (var role in roles)
            {
                await roleManager.CreateAsync(new IdentityRole(role));
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
