#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Configurations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// Boots the real application - the real pipeline, the real middleware order, the real endpoints -
/// over this assembly's PostgreSQL databases and a throwaway storage root.
/// </summary>
/// <remarks>
/// The point of this harness is that everything under test here has, until now, only ever been
/// checked by hand. Pass 2 A9 concluded cookie login could not be driven over HTTP; Pass 4B-H
/// disproved that but only as a one-off measurement, and in between a login regression reached a
/// browser. The matrices below are the ones that were being re-measured by hand every pass.
/// <para>
/// Nothing is stubbed or replaced: this is <c>WebApplicationFactory&lt;Program&gt;</c> over the
/// actual <c>Program.cs</c>, with configuration pointed at the test databases and a temporary
/// directory. If a middleware is registered in the wrong order, these tests see it.
/// </para>
/// <para>
/// <b>PostgreSQL only</b> (pass 47). The databases come from <see cref="UiTestDatabase"/>, so an unset
/// <c>GX_TEST_PG</c> fails the constructor with a message naming it; there is no file-database
/// fallback. By default the business database is reset before the host boots, so every fixture meets a
/// freshly provisioned installation, as it did when each factory had its own database file. The
/// temporary directory holds only the storage root and the mail sink.
/// </para>
/// </remarks>
public sealed class GxWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>A password that satisfies the configured Identity policy by construction.</summary>
    public const string KnownPassword = "Gx-Harness-Password-1!";

    private readonly string _environment;
    private readonly Dictionary<string, string?> _extraConfiguration;
    private readonly string _root;
    private readonly Action<IServiceCollection>? _configureServices;

    /// <param name="environment">The host environment; Development when omitted.</param>
    /// <param name="extraConfiguration">Settings applied over the harness defaults.</param>
    /// <param name="resetDatabase">
    /// True (the default): empty the business database before this host boots, so the boot
    /// provisions a fresh installation. False only for a host that must share the installation of
    /// one already running, as <see cref="ForcedPasswordChangeTests"/>' nested hosts do; resetting
    /// there would delete the rows the other host is serving. The log database is never reset.
    /// </param>
    /// <param name="configureServices">
    /// Registrations applied after the application's own, for example a capturing
    /// <c>ILogEventSink</c> or a replaced service.
    /// </param>
    public GxWebApplicationFactory(
        string? environment = null,
        Dictionary<string, string?>? extraConfiguration = null,
        bool resetDatabase = true,
        Action<IServiceCollection>? configureServices = null)
    {
        // Taken before the reset, so anything the boot provisions is stamped after it.
        ConstructedAtUtc = DateTime.UtcNow;

        _environment = environment ?? Environments.Development;
        _extraConfiguration = extraConfiguration ?? new Dictionary<string, string?>();
        _configureServices = configureServices;

        // No fallback: an unset GX_TEST_PG throws here, naming the variable.
        var business = UiTestDatabase.Business;
        if (resetDatabase) business.ResetAsync().GetAwaiter().GetResult();
        BusinessConnectionString = business.ConnectionString;
        LogConnectionString = UiTestDatabase.Logs.ConnectionString;

        _root = Path.Combine(Path.GetTempPath(), "gx-http-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    /// <summary>When this factory was created, before its reset and its boot (UTC).</summary>
    public DateTime ConstructedAtUtc { get; }

    /// <summary>The throwaway storage root this instance's disk provider is writing into.</summary>
    public string StorageRoot => Path.Combine(_root, "files");

    /// <summary>The throwaway directory the mail sink renders messages into.</summary>
    public string MailRoot => Path.Combine(_root, "mail");

    /// <summary>
    /// The two databases this fixture runs against, exposed so a test can inspect each one's schema
    /// directly. Proving that the business database has no SystemLogs table is the central claim of
    /// Pass 11, and it can only be made by looking at the business database itself.
    /// </summary>
    public string BusinessConnectionString { get; }

    /// <inheritdoc cref="BusinessConnectionString" />
    public string LogConnectionString { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);

        if (_configureServices is not null) builder.ConfigureTestServices(_configureServices);

        // The extras are ALSO applied as host settings. Under minimal hosting, ConfigureAppConfiguration
        // below is applied only when the host is built, after Program.cs has registered its services,
        // so a value read eagerly during registration never saw it (CO-151).
        foreach (var pair in _extraConfiguration) builder.UseSetting(pair.Key, pair.Value);

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            // This assembly's two databases on the GX_TEST_PG server, shared by every host in it,
            // which is why the assembly runs one test at a time.
            var settings = new Dictionary<string, string?>
            {
                ["DatabaseSettings:DBProvider"] = DbProviderKeys.Npgsql,
                ["DatabaseSettings:ConnectionString"] = BusinessConnectionString,

                // A SECOND database. Setting it is not optional housekeeping: an absent log
                // connection string is a supported, non-fatal state, so leaving it out would let
                // every test here pass while proving nothing about the log database - the tests
                // would be exercising the "not configured" path without saying so.
                ["DatabaseSettings:LogConnectionString"] = LogConnectionString,

                // The disk storage provider, rooted where this fixture can delete it afterwards.
                ["Storage:Provider"] = StorageProviderKeys.Disk,
                ["Storage:RootPath"] = StorageRoot,

                // The mail sink, stated rather than inferred. The harness runs under Development so
                // it would default to the sink anyway, but a test suite that could send real email
                // if somebody changed its environment name is not a risk worth carrying. The path is
                // under the same root this fixture deletes in Dispose.
                ["Mail:Delivery"] = nameof(MailDelivery.Sink),
                ["Mail:SinkPath"] = MailRoot,

                ["AppConfigurationSettings:AppName"] = "GX Application",
                ["AppConfigurationSettings:DefaultTimeZone"] = "UTC",

                // Quiet: these tests assert on status codes, not on log output.
                ["Serilog:MinimumLevel:Default"] = "Warning"
            };

            foreach (var pair in _extraConfiguration) settings[pair.Key] = pair.Value;

            configuration.AddInMemoryCollection(settings);
        });
    }

    /// <summary>
    /// A client that does NOT follow redirects, because the redirect IS the assertion in most of
    /// these tests - an anonymous request being bounced to the login page, or a flagged user being
    /// held on the change-password page.
    /// </summary>
    /// <remarks>
    /// The base address is <b>https</b>, and that is load-bearing rather than decorative. The
    /// application sets <c>options.Cookie.SecurePolicy = CookieSecurePolicy.Always</c> on the
    /// Identity cookie - correctly - so over an http base address the sign-in cookie is emitted as
    /// Secure, the cookie container declines to store it, and every authenticated request looks
    /// exactly like a failed login. That is a plausible reason to conclude "cookie login cannot be
    /// driven from a test", and it is wrong: it is one line of client configuration.
    /// TestServer does no real TLS; an https base address simply makes Request.IsHttps true.
    /// </remarks>
    public HttpClient CreateNonRedirectingClient() =>
        CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
            BaseAddress = new Uri("https://localhost")
        });

    /// <summary>
    /// Gives the seeded administrator a password the tests know, and puts the MustChangePassword
    /// flag into a stated state.
    /// </summary>
    /// <remarks>
    /// The bootstrap generates a random password and prints it once, deliberately - so there is no
    /// way to sign in as that account from a test without resetting it. This resets it through
    /// <see cref="UserManager{TUser}"/>, exactly as an operator would, rather than reaching into
    /// the database.
    /// <para>
    /// <paramref name="mustChangePassword"/> is SET, not merely cleared, and that distinction is
    /// load-bearing. A reset host starts from the bootstrap's state (flag on), but a host built
    /// with <c>resetDatabase: false</c> shares an installation another host has already changed,
    /// and a helper that only ever cleared would leave that fixture testing nothing. Stating the
    /// flag makes every fixture independent of what ran before it.
    /// </para>
    /// </remarks>
    public async Task<ApplicationUser> ResetAdministratorPasswordAsync(bool mustChangePassword = false)
    {
        using var scope = Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var administrator = await userManager.FindByNameAsync(Users.Administrator)
                            ?? throw new InvalidOperationException(
                                "The bootstrap did not provision an administrator; the harness has nothing to sign in as.");

        var token = await userManager.GeneratePasswordResetTokenAsync(administrator);
        var reset = await userManager.ResetPasswordAsync(administrator, token, KnownPassword);
        if (!reset.Succeeded)
        {
            throw new InvalidOperationException(
                "Could not reset the administrator password: " + string.Join(", ", reset.Errors.Select(e => e.Description)));
        }

        if (administrator.MustChangePassword != mustChangePassword)
        {
            administrator.MustChangePassword = mustChangePassword;
            await userManager.UpdateAsync(administrator);
        }

        return administrator;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing) return;
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A log file the host still holds open is not a test failure.
        }
    }
}
#nullable restore
