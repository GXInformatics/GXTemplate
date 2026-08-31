using System.Security.Claims;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Configurations;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using CleanArchitecture.Blazor.Infrastructure.Services.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZiggyCreatures.Caching.Fusion;

namespace CleanArchitecture.Blazor.Infrastructure.UnitTests.Security;

/// <summary>
/// A context factory over one open in-memory SQLite connection, so every context sees the same
/// database for the life of a test.
/// </summary>
internal sealed class TestDbContextFactory(DbContextOptions<ApplicationDbContext> options)
    : IDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext() => new(options);
}

/// <summary>
/// The effective-policy arithmetic: what an administrator sets, what a user may do to it, and what
/// the deployment's bounds do to both.
/// </summary>
/// <remarks>
/// The direction of the user preference is the security-relevant part and the easy one to get
/// backwards. An idle timeout guards an unattended workstation; a user who could LENGTHEN their own
/// would simply set it to eight hours and the control would be gone. These tests pin the asymmetry
/// in the place that enforces it - read time - rather than only in the screen that offers it.
/// </remarks>
public class IdleTimeoutPolicyProviderTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _factory;

    public IdleTimeoutPolicyProviderTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        using (var db = new ApplicationDbContext(options))
        {
            db.Database.EnsureCreated();
        }

        _factory = new TestDbContextFactory(options);
    }

    public void Dispose() => _connection.Dispose();

    private IdleTimeoutPolicyProvider Provider(IdleTimeoutSettings? settings = null) => new(
        _factory,
        // A fresh cache per provider, so one test's invalidation semantics cannot leak into another.
        new FusionCache(new FusionCacheOptions()),
        settings ?? new IdleTimeoutSettings(),
        NullLogger<IdleTimeoutPolicyProvider>.Instance);

    private static ClaimsPrincipal User(string id = "u1") =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "test"));

    private async Task SetUserPreferenceAsync(string userId, int? minutes)
    {
        await using var db = _factory.CreateDbContext();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = userId, IdleTimeoutMinutes = minutes });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task WithNoRow_TheAdministeredPolicyIsSeededFromConfiguration()
    {
        // A database provisioned before this feature existed has no row. The first read writes one
        // rather than requiring a data migration.
        var settings = new IdleTimeoutSettings { DefaultIdleTimeoutMinutes = 25, DefaultCountdownSeconds = 45 };

        var administered = await Provider(settings).GetAdministeredAsync();

        Assert.Equal(25, administered.IdleMinutes);
        Assert.Equal(45, administered.CountdownSeconds);

        await using var db = _factory.CreateDbContext();
        Assert.Single(db.SecurityPolicies);
    }

    [Fact]
    public async Task AStoredPolicyOutsideTheBounds_IsClampedOnTheWayOut()
    {
        // Not merely on save. The authentication cookie is sized from these bounds, so a row that
        // predates a tightening - or was edited around the screen - must not reach enforcement.
        await using (var db = _factory.CreateDbContext())
        {
            db.SecurityPolicies.Add(new SecurityPolicy { IdleTimeoutMinutes = 9_000, CountdownSeconds = 60 });
            await db.SaveChangesAsync();
        }

        var settings = new IdleTimeoutSettings { MinIdleTimeoutMinutes = 5, MaxIdleTimeoutMinutes = 60 };

        var administered = await Provider(settings).GetAdministeredAsync();

        Assert.Equal(60, administered.IdleMinutes);
    }

    [Fact]
    public async Task AUserPreferenceShorterThanThePolicy_IsHonoured()
    {
        await SetUserPreferenceAsync("u1", 5);
        await using (var db = _factory.CreateDbContext())
        {
            db.SecurityPolicies.Add(new SecurityPolicy { IdleTimeoutMinutes = 30, CountdownSeconds = 60 });
            await db.SaveChangesAsync();
        }

        var effective = await Provider().GetEffectiveAsync(User());

        Assert.Equal(5, effective.IdleMinutes);
    }

    [Fact]
    public async Task AUserPreferenceLongerThanThePolicy_IsIgnored()
    {
        // The whole asymmetry, in one assertion. If this ever reads 240 the control is gone: anyone
        // who finds the timeout inconvenient can simply opt out of it.
        await SetUserPreferenceAsync("u1", 240);
        await using (var db = _factory.CreateDbContext())
        {
            db.SecurityPolicies.Add(new SecurityPolicy { IdleTimeoutMinutes = 30, CountdownSeconds = 60 });
            await db.SaveChangesAsync();
        }

        var effective = await Provider().GetEffectiveAsync(User());

        Assert.Equal(30, effective.IdleMinutes);
    }

    [Fact]
    public async Task AUserPreferenceBelowTheFloor_IsClamped()
    {
        // "Clamped at read time" - the case where a value was forced into the database directly.
        await SetUserPreferenceAsync("u1", 0);
        var settings = new IdleTimeoutSettings { MinIdleTimeoutMinutes = 3 };

        var effective = await Provider(settings).GetEffectiveAsync(User());

        Assert.True(effective.IdleMinutes >= 3);
    }

    [Fact]
    public async Task WithUserOverrideSwitchedOff_AnExistingPreferenceIsIgnored()
    {
        // Ignored rather than honoured: turning the option off is a decision that users do not set
        // this, and a preference saved while it was on must not outlive that decision.
        await SetUserPreferenceAsync("u1", 5);
        await using (var db = _factory.CreateDbContext())
        {
            db.SecurityPolicies.Add(new SecurityPolicy { IdleTimeoutMinutes = 30, CountdownSeconds = 60 });
            await db.SaveChangesAsync();
        }

        var settings = new IdleTimeoutSettings { AllowUserOverride = false };

        var effective = await Provider(settings).GetEffectiveAsync(User());

        Assert.Equal(30, effective.IdleMinutes);
    }

    [Fact]
    public async Task TheCountdownIsNeverNarrowedByAUserPreference()
    {
        // It is a warning, not a policy: how long the dialog is shown is not the user's to shorten.
        await SetUserPreferenceAsync("u1", 5);
        await using (var db = _factory.CreateDbContext())
        {
            db.SecurityPolicies.Add(new SecurityPolicy { IdleTimeoutMinutes = 30, CountdownSeconds = 45 });
            await db.SaveChangesAsync();
        }

        var effective = await Provider().GetEffectiveAsync(User());

        Assert.Equal(45, effective.CountdownSeconds);
    }

    [Fact]
    public async Task WhenTheFeatureIsOff_TheEffectivePolicyIsDisabled()
    {
        var effective = await Provider(new IdleTimeoutSettings { Enabled = false }).GetEffectiveAsync(User());

        Assert.False(effective.Enabled);
    }

    [Fact]
    public async Task InvalidatingThePolicy_MakesTheNextReadSeeTheNewValue()
    {
        // The mechanism by which an administrator's change reaches sessions already open. Without
        // the invalidation the cached policy would stand until its (deliberately long) duration
        // elapsed, and "takes effect on live sessions" would be false.
        var provider = Provider();

        await using (var db = _factory.CreateDbContext())
        {
            db.SecurityPolicies.Add(new SecurityPolicy { IdleTimeoutMinutes = 30, CountdownSeconds = 60 });
            await db.SaveChangesAsync();
        }

        Assert.Equal(30, (await provider.GetAdministeredAsync()).IdleMinutes);

        await using (var db = _factory.CreateDbContext())
        {
            var row = db.SecurityPolicies.Single();
            row.IdleTimeoutMinutes = 2;
            await db.SaveChangesAsync();
        }

        Assert.Equal(30, (await provider.GetAdministeredAsync()).IdleMinutes);   // still cached

        provider.Invalidate();

        Assert.Equal(2, (await provider.GetAdministeredAsync()).IdleMinutes);
    }

    [Fact]
    public async Task InvalidatingOneUser_MakesTheNextReadSeeTheirNewPreference()
    {
        var provider = Provider();
        await SetUserPreferenceAsync("u1", 20);

        // The administered window has to be wider than either preference, or the min() would be what
        // this test observed rather than the cache.
        await using (var db = _factory.CreateDbContext())
        {
            db.SecurityPolicies.Add(new SecurityPolicy { IdleTimeoutMinutes = 30, CountdownSeconds = 60 });
            await db.SaveChangesAsync();
        }

        Assert.Equal(20, (await provider.GetEffectiveAsync(User())).IdleMinutes);

        await using (var db = _factory.CreateDbContext())
        {
            var user = db.Users.Single(u => u.Id == "u1");
            user.IdleTimeoutMinutes = 4;
            await db.SaveChangesAsync();
        }

        provider.InvalidateUser("u1");

        Assert.Equal(4, (await provider.GetEffectiveAsync(User())).IdleMinutes);
    }
}

/// <summary>
/// The server-side enforcement: what actually ends a session, independent of any browser.
/// </summary>
public class IdleSessionEnforcerTests
{
    private sealed class StubPolicy(IdleTimeoutPolicy policy, bool enabled = true) : IIdleTimeoutPolicyProvider
    {
        public bool Enabled => enabled;
        public Task<AdministeredIdleTimeoutPolicy> GetAdministeredAsync(CancellationToken ct = default) =>
            Task.FromResult(new AdministeredIdleTimeoutPolicy(policy.IdleMinutes, policy.CountdownSeconds));
        public Task<IdleTimeoutPolicy> GetEffectiveAsync(ClaimsPrincipal user, CancellationToken ct = default) =>
            Task.FromResult(policy);
        public void Invalidate() { }
        public void InvalidateUser(string userId) { }
    }

    private static CookieValidatePrincipalContext Context(
        DateTimeOffset? lastActivity, string path = "/some/page", DateTimeOffset? issued = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = path;

        var principal = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "u1")], "cookie"));

        var properties = new AuthenticationProperties { IssuedUtc = issued ?? DateTimeOffset.UtcNow };
        if (lastActivity is { } stamp)
        {
            properties.Items[IdleSessionEnforcer.LastActivityKey] =
                stamp.ToUnixTimeMilliseconds().ToString();
        }

        var ticket = new AuthenticationTicket(principal, properties, "Identity.Application");

        return new CookieValidatePrincipalContext(
            httpContext,
            new AuthenticationScheme("Identity.Application", null, typeof(CookieAuthenticationHandler)),
            new CookieAuthenticationOptions(),
            ticket);
    }

    private static IdleSessionEnforcer Enforcer(int idleMinutes = 15, int countdownSeconds = 60, bool enabled = true) =>
        new(new StubPolicy(new IdleTimeoutPolicy(enabled, idleMinutes, countdownSeconds), enabled),
            NullLogger<IdleSessionEnforcer>.Instance);

    [Fact]
    public async Task ASessionInsideItsWindow_Survives()
    {
        var context = Context(DateTimeOffset.UtcNow.AddMinutes(-5));

        Assert.True(await Enforcer(idleMinutes: 15).IsStillValidAsync(context));
    }

    [Fact]
    public async Task ASessionPastIdlePlusCountdown_IsRejected()
    {
        // The window is idle + countdown, not idle alone: while the warning is counting down the
        // user may still click Stay Logged In, and the server must not have ended the session
        // underneath the dialog it is showing.
        var context = Context(DateTimeOffset.UtcNow.AddMinutes(-16).AddSeconds(-1));

        Assert.False(await Enforcer(idleMinutes: 15, countdownSeconds: 60).IsStillValidAsync(context));
    }

    [Fact]
    public async Task DuringTheCountdown_TheSessionStillSurvives()
    {
        var context = Context(DateTimeOffset.UtcNow.AddMinutes(-15).AddSeconds(-30));

        Assert.True(await Enforcer(idleMinutes: 15, countdownSeconds: 60).IsStillValidAsync(context));
    }

    [Fact]
    public async Task AKeepAliveRequest_StampsActivityAndRenewsTheTicket()
    {
        var context = Context(DateTimeOffset.UtcNow.AddMinutes(-5), IdleTimeoutRoutes.KeepAlive);

        Assert.True(await Enforcer().IsStillValidAsync(context));
        Assert.True(context.ShouldRenew);
        Assert.True(context.Properties.Items.ContainsKey(IdleSessionEnforcer.LastActivityKey));
    }

    [Fact]
    public async Task AnyOtherRequest_DoesNotCountAsActivity()
    {
        // The load-bearing negative. If an ordinary authenticated request renewed the window, an
        // unattended workstation would keep itself signed in through whatever its browser happened
        // to fetch, and the idle timeout would never fire.
        var stamp = DateTimeOffset.UtcNow.AddMinutes(-5);
        var context = Context(stamp, "/system/audittrails");

        Assert.True(await Enforcer().IsStillValidAsync(context));
        Assert.False(context.ShouldRenew);
        Assert.Equal(
            stamp.ToUnixTimeMilliseconds().ToString(),
            context.Properties.Items[IdleSessionEnforcer.LastActivityKey]);
    }

    [Fact]
    public async Task WithNoStampYet_TheTicketsIssueTimeIsUsed()
    {
        // A freshly issued ticket carries no stamp. Treating "absent" as the epoch would sign every
        // user out on their first request after signing in.
        var context = Context(lastActivity: null, issued: DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.True(await Enforcer(idleMinutes: 15).IsStillValidAsync(context));
    }

    [Fact]
    public async Task WithNoStampAndAnOldTicket_TheSessionIsRejected()
    {
        var context = Context(lastActivity: null, issued: DateTimeOffset.UtcNow.AddHours(-3));

        Assert.False(await Enforcer(idleMinutes: 15).IsStillValidAsync(context));
    }

    [Fact]
    public async Task WhenTheFeatureIsOff_NothingIsEverRejected()
    {
        var context = Context(DateTimeOffset.UtcNow.AddDays(-2));

        Assert.True(await Enforcer(enabled: false).IsStillValidAsync(context));
    }
}

/// <summary>
/// The startup validation. These values size the authentication cookie, so a bad combination has to
/// fail the process rather than produce sessions that end at a time nobody chose.
/// </summary>
public class IdleTimeoutSettingsValidationTests
{
    private static string[] Errors(IdleTimeoutSettings settings) =>
        settings.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(settings))
            .Select(r => r.ErrorMessage!)
            .ToArray();

    [Fact]
    public void TheShippedDefaults_AreValid()
    {
        // Worth asserting explicitly: the defaults put the countdown (60s) exactly at the shortest
        // permitted window (1 minute). Equal is allowed, exceeding is not - so an off-by-one in the
        // comparison would fail every generated project at startup.
        Assert.Empty(Errors(new IdleTimeoutSettings()));
    }

    [Fact]
    public void ACountdownLongerThanTheShortestWindow_FailsStartup()
    {
        var errors = Errors(new IdleTimeoutSettings { MinIdleTimeoutMinutes = 1, DefaultCountdownSeconds = 90 });

        Assert.Contains(errors, e => e.Contains("exceeds the shortest idle window"));
    }

    [Fact]
    public void AMaximumAboveEightHours_FailsStartup()
    {
        var errors = Errors(new IdleTimeoutSettings { MaxIdleTimeoutMinutes = 600 });

        Assert.Contains(errors, e => e.Contains(nameof(IdleTimeoutSettings.MaxIdleTimeoutMinutes)));
    }

    [Fact]
    public void ADefaultOutsideTheBounds_FailsStartup()
    {
        var errors = Errors(new IdleTimeoutSettings
        {
            MinIdleTimeoutMinutes = 10,
            MaxIdleTimeoutMinutes = 20,
            DefaultIdleTimeoutMinutes = 45,
            DefaultCountdownSeconds = 30
        });

        Assert.Contains(errors, e => e.Contains(nameof(IdleTimeoutSettings.DefaultIdleTimeoutMinutes)));
    }

    [Fact]
    public void AMaximumBelowTheMinimum_FailsStartup()
    {
        var errors = Errors(new IdleTimeoutSettings { MinIdleTimeoutMinutes = 60, MaxIdleTimeoutMinutes = 30 });

        Assert.Contains(errors, e => e.Contains("must be greater than"));
    }

    [Fact]
    public void WhenTheFeatureIsOff_NothingIsValidated()
    {
        // The values are inert when the feature is off; failing a start over a setting that does
        // nothing would be noise.
        var errors = Errors(new IdleTimeoutSettings
        {
            Enabled = false,
            MinIdleTimeoutMinutes = 0,
            MaxIdleTimeoutMinutes = 9_999,
            DefaultCountdownSeconds = 5_000
        });

        Assert.Empty(errors);
    }

    [Fact]
    public void TheCookieLifetime_CoversTheWidestWindowPlusCountdownAndGrace()
    {
        // The cookie must outlive the longest session any policy could produce, or enforcement would
        // never get to run - the cookie would expire first and the user would be bounced to login
        // with no explanation.
        var settings = new IdleTimeoutSettings
        {
            MaxIdleTimeoutMinutes = 120,
            DefaultCountdownSeconds = 60,
            CookieGraceMinutes = 2
        };

        Assert.Equal(TimeSpan.FromMinutes(123), settings.CookieLifetime);
    }
}
