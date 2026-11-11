using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.PublishStrategies;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Persistence.Rules;
using CleanArchitecture.Blazor.TestSupport;
using Mediator;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using FluentAssertions;
using Moq;
using Npgsql;

namespace CleanArchitecture.Blazor.Application.IntegrationTests.DataFoundations;

/// <summary>
/// Pass 59's own harness: the application's services over <see cref="TestModelDbContext"/>, in a database
/// of its own, so the rules can be exercised on a ledger, a posted document and a transfer the template
/// does not ship.
/// </summary>
/// <remarks>
/// The schema is rebuilt from the model on first use (EnsureCreated, after dropping every schema of this
/// throwaway database - never the database). The append-only triggers are applied with the same SQL the
/// migration helper emits. Tests use unique values rather than a reset.
/// </remarks>
public static class ModelHarness
{
    public const string DatabaseName = TestDatabaseNames.ProjectPrefix + "appint_model";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static IServiceProvider? _root;

    public static PushAccessor Accessor { get; } = new();

    public static async Task<IServiceProvider> RootAsync()
    {
        if (_root is not null) return _root;
        await Gate.WaitAsync();
        try
        {
            if (_root is not null) return _root;

            var database = PostgresTestDatabase.FromEnvironment(DatabaseName);
            await database.EnsureExistsAsync();

            var services = Build(database.ConnectionString);
            using (var scope = services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                // (P60) Every schema the model uses - the module schemas included - read from the model rather
                // than named here, plus "core", which Pass 59's databases still hold. Never the database.
                var schemas = db.Model.GetEntityTypes().Select(e => e.GetSchema()).OfType<string>()
                    .Append("core").Distinct().ToList();
                var drop = string.Concat(schemas.Select(s => $"DROP SCHEMA IF EXISTS \"{s}\" CASCADE; "))
                           + "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;";
                await using (var connection = new NpgsqlConnection(database.ConnectionString))
                {
                    await connection.OpenAsync();
                    await using var command = new NpgsqlCommand(drop, connection);
                    await command.ExecuteNonQueryAsync();
                }

                await db.Database.EnsureCreatedAsync();
                var ledger = db.Model.FindEntityType(typeof(TestLedgerRow))!;
                foreach (var sql in AppendOnlyMigrationExtensions.CreateSql(ledger.GetTableName()!, ledger.GetSchema()))
                    await db.Database.ExecuteSqlRawAsync(sql);
            }

            _root = services;
            return services;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static IServiceProvider Build(string connectionString)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", true, true)
            .AddEnvironmentVariables()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DatabaseSettings:DBProvider"] = DbProviderKeys.Npgsql,
                ["DatabaseSettings:ConnectionString"] = connectionString,
                ["DatabaseSettings:LogConnectionString"] = string.Empty
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton(Mock.Of<IWebHostEnvironment>(w => w.EnvironmentName == "Development" && w.ApplicationName == "Server.UI"));
        services.AddSingleton<IHostEnvironment>(p => p.GetRequiredService<IWebHostEnvironment>());
        services.AddInfrastructure(configuration).AddApplication();
        services.AddMediator(options =>
        {
            options.Assemblies = [typeof(CleanArchitecture.Blazor.Application.DependencyInjection)];
            options.NotificationPublisherType = typeof(ChannelBasedNoWaitPublisher);
            options.ServiceLifetime = ServiceLifetime.Scoped;
        });

        // The test assembly's posting handlers (ScriptedPostingEventHandler), as Testing registers them.
        CleanArchitecture.Blazor.Application.Common.Transactions.PostingEventHandlerRegistration
            .AddPostingEventHandlers(services, typeof(ModelHarness).Assembly);

        // Pass 60: the test approval permissions are policies, as the application's own are.
        services.AddAuthorizationCore(options =>
        {
            foreach (var permission in new[] { TestApprovalPermissions.Approve, TestApprovalPermissions.ApprovePlain })
                options.AddPolicy(permission, policy => policy.RequireClaim(ApplicationClaimTypes.Permission, permission));
        });

        services.RemoveAll<IUserContextAccessor>();
        services.AddSingleton<IUserContextAccessor>(Accessor);

        // The one substitution: every context the application builds is the test model's.
        services.RemoveAll<IDbContextFactory<ApplicationDbContext>>();
        services.AddScoped<IDbContextFactory<ApplicationDbContext>>(p =>
            new Factory(p.GetRequiredService<DbContextOptions<ApplicationDbContext>>(), p.GetRequiredService<IUserContextAccessor>()));

        return services.BuildServiceProvider();
    }

    private sealed class Factory(DbContextOptions<ApplicationDbContext> options, IUserContextAccessor accessor)
        : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new TestModelDbContext(options, accessor);
    }

    // ---- principals ---------------------------------------------------------------------------

    /// <summary>A user context with the given branch access, for the filter and the save rules.</summary>
    public static UserContext Reader(string userId, BranchAccess branches) =>
        new(UserId: userId, UserName: userId, Branches: branches);

    public static BranchAccess Reads(params int[] ids) => new(false, ids, ids, false, ids.FirstOrDefault());

    public static BranchAccess ReadsAll(params int[] writeIds) => new(true, writeIds, writeIds, false, null);

    public static UserContext System { get; } = new(UserId: "system", UserName: Users.System, Branches: BranchAccess.System);

    /// <summary>Creates a user, optionally assigned to branches and holding permissions (through a role).</summary>
    public static async Task<string> CreateUserAsync(int[] assignedBranches, params string[] permissions)
    {
        var services = await RootAsync();
        using var scope = services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var name = "p59-" + Guid.NewGuid().ToString("N")[..12];
        var user = new ApplicationUser { UserName = name, Email = name + "@example.test", IsActive = true };
        (await userManager.CreateAsync(user, "Password123!")).Succeeded.Should().BeTrue();

        if (permissions.Length > 0)
        {
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            var role = new ApplicationRole(name + "-role");
            await roleManager.CreateAsync(role);
            foreach (var permission in permissions)
                await roleManager.AddClaimAsync(role, new Claim(ApplicationClaimTypes.Permission, permission));
            await userManager.AddToRoleAsync(user, role.Name!);
        }

        if (assignedBranches.Length > 0)
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            foreach (var branchId in assignedBranches)
                db.UserBranches.Add(new UserBranch { UserId = user.Id, BranchId = branchId, IsDefault = branchId == assignedBranches[0] });
            await db.SaveChangesAsync();
        }
        return user.Id;
    }

    /// <summary>Creates a branch and returns its id.</summary>
    public static async Task<int> CreateBranchAsync(bool active = true)
    {
        var services = await RootAsync();
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var branch = new Branch { Code = "B" + Guid.NewGuid().ToString("N")[..8], Name = "Branch", IsActive = active };
        db.Branches.Add(branch);
        await db.SaveChangesAsync();
        return branch.Id;
    }

    /// <summary>A context of the test model, as the application builds one, under <paramref name="user"/>.</summary>
    public static async Task<(IServiceScope Scope, TestModelDbContext Db)> ContextAsync()
    {
        var services = await RootAsync();
        var scope = services.CreateScope();
        var db = (TestModelDbContext)await scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
            .CreateDbContextAsync();
        return (scope, db);
    }

    /// <summary>An ambient-user accessor that honours pushes, with no user by default.</summary>
    public sealed class PushAccessor : IUserContextAccessor
    {
        private sealed class Node
        {
            public UserContext? Value;
            public Node? Parent;
        }

        private static readonly AsyncLocal<Node?> Pushed = new();

        public UserContext? Current => Pushed.Value?.Value;

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
}
