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

                // (P60c) EVERY user schema of this harness's own throwaway database, read from the catalogue: the
                // model's (core), and any an earlier template left behind (Pass 60's module schemas tdf, tsa, tsb).
                // EnsureCreated skips the whole model when ANY table exists, so one leftover schema with a table in it
                // meant core was never created. Never the database.
                var schemas = new List<string>();
                await using (var connection = new NpgsqlConnection(database.ConnectionString))
                {
                    await connection.OpenAsync();
                    await using (var list = new NpgsqlCommand(
                        "SELECT nspname FROM pg_namespace WHERE nspname NOT LIKE 'pg\\_%' AND nspname <> 'information_schema'", connection))
                    await using (var reader = await list.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync()) schemas.Add(reader.GetString(0));
                    }

                    var drop = string.Concat(schemas.Select(s => $"DROP SCHEMA IF EXISTS \"{s.Replace("\"", "\"\"")}\" CASCADE; "))
                               + "CREATE SCHEMA IF NOT EXISTS public;";
                    await using var command = new NpgsqlCommand(drop, connection);
                    await command.ExecuteNonQueryAsync();
                }

                await db.Database.EnsureCreatedAsync();
                // (P60c) Every append-only table in the model, as the migrations would give each one - not only
                // TestLedgerRow. The test model is built on the real context, so in a generated project it also holds
                // the application's own ledgers, and T19's "no table lacks its trigger" would otherwise fail there.
                var ledgers = db.Model.GetEntityTypes()
                    .Where(t => !t.IsOwned() && t.BaseType is null && typeof(CleanArchitecture.Blazor.Domain.Common.Entities.IAppendOnly).IsAssignableFrom(t.ClrType) && t.GetTableName() is not null);
                foreach (var ledger in ledgers)
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

        // Pass 60b (row 13): a module's own rules for AddEditBranchCommand, beside the template's, as a generated project's
        // AddValidatorsFromAssembly would register them. Inert unless a test switches it on.
        services.AddScoped<FluentValidation.IValidator<CleanArchitecture.Blazor.Application.Features.Branches.AddEditBranchCommand>,
            CleanArchitecture.Blazor.Application.IntegrationTests.Extensions.TestModuleBranchValidator>();

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
        var branch = new Branch { Code = TestBranches.NewCode(), Name = TestBranches.NewName("Branch"), IsActive = active }; // (P60c, T9) floor-safe, unique
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
