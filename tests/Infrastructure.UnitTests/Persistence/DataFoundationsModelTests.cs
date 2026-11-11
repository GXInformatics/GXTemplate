using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Infrastructure;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Services.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CleanArchitecture.Blazor.Infrastructure.UnitTests.Persistence;

/// <summary>Pass 59 plan §5 rows 9 and 27 (the hub filter's refresh). No database is touched.</summary>
public class DataFoundationsModelTests
{
    private const string PostgreSqlMigrations = "CleanArchitecture.Blazor.Migrators.PostgreSQL";

    private static ApplicationDbContext PostgreSqlContext()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<IdentityOptions>(DependencyInjection.ConfigureIdentityOptions);
        return new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .EnableServiceProviderCaching(false)
            .UseApplicationServiceProvider(services.BuildServiceProvider())
            .UseNpgsql("Host=model-only", o => o.MigrationsAssembly(PostgreSqlMigrations))
            .Options);
    }

    [Fact]
    public void T09_OnPostgreSql_VersionIsTheXminSystemColumn_AsARowVersion()
    {
        using var db = PostgreSqlContext();
        foreach (var type in new[] { typeof(PicklistSet), typeof(Branch) })
        {
            var version = db.Model.FindEntityType(type)!.FindProperty("Version")!;
            Assert.Equal("xmin", version.GetColumnName());
            Assert.Equal("xid", version.GetColumnType());
            Assert.True(version.IsConcurrencyToken, type.Name);
            Assert.Equal(ValueGenerated.OnAddOrUpdate, version.ValueGenerated);
        }

        Assert.Null(db.Model.FindEntityType(typeof(SecurityPolicy))!.FindProperty("Version"));
    }

    [Fact]
    public void T09_ThePostgreSqlMigrationCreatesNoXminColumn()
    {
        // The C# migration lists an AddColumn for xmin (EF's differ sees a property), but Npgsql's SQL
        // generator skips system columns. What reaches the database is what matters, so this reads the SQL.
        using var db = PostgreSqlContext();
        var migrator = db.GetService<IMigrator>();
        var migrations = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrationsAssembly>().Migrations.Keys;
        string? previous = null, dataFoundations = null;
        foreach (var id in migrations)
        {
            if (id.EndsWith("_DataFoundations", StringComparison.Ordinal)) { dataFoundations = id; break; }
            previous = id;
        }
        Assert.NotNull(dataFoundations);

        var sql = migrator.GenerateScript(previous, dataFoundations);
        Assert.DoesNotContain("xmin", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CREATE TABLE \"Branches\"", sql);
        Assert.Contains("ON DELETE RESTRICT", sql);
        Assert.Contains("'Permissions.Users.Delete'", sql);
    }

    // ---- 27: the hub filter re-reads the context on each invocation ---------------------------

    private sealed class Loader(UserContext? fresh) : IUserContextLoader
    {
        public int Loads { get; private set; }

        public Task<UserContext?> LoadAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
        {
            Loads++;
            return Task.FromResult(fresh);
        }

        public void ClearUserContextCache(string userId) { }
    }

    private sealed class Caller(ClaimsPrincipal user) : HubCallerContext
    {
        public override string ConnectionId => "c1";
        public override string? UserIdentifier => "u1";
        public override ClaimsPrincipal? User => user;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public bool Aborted { get; private set; }
        public override void Abort() => Aborted = true;
    }

    private sealed class EmptyHub : Hub;

    private static async Task<(UserContext? Seen, Caller Caller)> InvokeAsync(UserContext snapshot, UserContext? fresh)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IUserContextLoader>(new Loader(fresh));
        var provider = services.BuildServiceProvider();
        var accessor = new UserContextAccessor();
        var filter = new UserContextHubFilter(provider.GetRequiredService<IServiceScopeFactory>(), accessor, new ClientInfoAccessor());

        var caller = new Caller(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "u1")], "test")));
        caller.Items[HubUserContext.ItemsKey] = snapshot;
        var invocation = new HubInvocationContext(caller, provider, new EmptyHub(),
            typeof(EmptyHub).GetMethod(nameof(Hub.Dispose))!, Array.Empty<object?>());

        UserContext? seen = null;
        await filter.InvokeMethodAsync(invocation, _ => { seen = accessor.Current; return ValueTask.FromResult<object?>(null); });
        return (seen, caller);
    }

    [Fact]
    public async Task T27_EachInvocationSeesTheReloadedContext_KeepingTheConnectionsIpAddress()
    {
        var snapshot = new UserContext("u1", "u1", IpAddress: "10.0.0.7", Branches: new BranchAccess(false, [1, 2], [1, 2], false, 1));
        var fresh = new UserContext("u1", "u1", Branches: new BranchAccess(false, [1], [1], false, 1));

        var (seen, caller) = await InvokeAsync(snapshot, fresh);

        Assert.NotNull(seen);
        Assert.Equal(new[] { 1 }, seen!.Branches!.ReadIds);
        Assert.Equal("10.0.0.7", seen.IpAddress);
        Assert.Same(seen, caller.Items[HubUserContext.ItemsKey]);
    }

    [Fact]
    public async Task T27_AUserWithNoContext_IsRefusedAndDisconnected()
    {
        // (P60) Pass 59 let the invocation run with no user context. Since Pass 60 (Part D item 4) a principal whose
        // context is gone - deleted, deactivated, or locked out by an administrator - is refused: the hub method does
        // not run and the connection is closed.
        var snapshot = new UserContext("u1", "u1", Branches: new BranchAccess(false, [1], [1], false, 1));
        var services = new ServiceCollection();
        services.AddSingleton<IUserContextLoader>(new Loader(null));
        var provider = services.BuildServiceProvider();
        var filter = new UserContextHubFilter(provider.GetRequiredService<IServiceScopeFactory>(), new UserContextAccessor(), new ClientInfoAccessor());
        var caller = new Caller(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "u1")], "test")));
        caller.Items[HubUserContext.ItemsKey] = snapshot;
        var invocation = new HubInvocationContext(caller, provider, new EmptyHub(), typeof(EmptyHub).GetMethod(nameof(Hub.Dispose))!, Array.Empty<object?>());

        var ran = false;
        await Assert.ThrowsAsync<HubException>(async () =>
            await filter.InvokeMethodAsync(invocation, _ => { ran = true; return ValueTask.FromResult<object?>(null); }));

        Assert.False(ran);
        Assert.True(caller.Aborted);
        Assert.False(caller.Items.ContainsKey(HubUserContext.ItemsKey));
    }
}
