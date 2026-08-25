using System.Security.Cryptography;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Extensions;
using Microsoft.Extensions.Options;

namespace CleanArchitecture.Blazor.Infrastructure.Persistence;

/// <summary>
/// Brings a database up to a state the application can actually run against.
/// <para>
/// Three stages, deliberately separate because they belong in different environments:
/// <see cref="InitialiseAsync"/> applies migrations (always), <see cref="ProvisionAsync"/> creates
/// the things the application cannot function without (always), and
/// <see cref="SeedSampleDataAsync"/> adds material that only exists to make a development
/// environment pleasant (Development only).
/// </para>
/// <para>
/// Before Pass 7-3 the second and third were one method behind an <c>IsDevelopment()</c> gate, so a
/// Production deployment came up with a correct, completely empty schema and no account to log in
/// with. Every stage is idempotent: a second start provisions nothing and logs nothing.
/// </para>
/// </summary>
public class ApplicationDbContextInitializer
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<ApplicationDbContextInitializer> _logger;
    private readonly RoleManager<ApplicationRole> _roleManager;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IdentityOptions _identityOptions;

    public ApplicationDbContextInitializer(ILogger<ApplicationDbContextInitializer> logger,
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        UserManager<ApplicationUser> userManager,
        RoleManager<ApplicationRole> roleManager,
        IOptions<IdentityOptions> identityOptions)
    {
        _logger = logger;
        _context = dbContextFactory.CreateDbContext();
        _userManager = userManager;
        _roleManager = roleManager;
        _identityOptions = identityOptions.Value;
    }

    public async Task InitialiseAsync()
    {
        try
        {
            if (_context.Database.IsRelational())
                await _context.Database.MigrateAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while initialising the database");
            throw;
        }
    }

    /// <summary>
    /// Everything the application needs to be usable at all, in EVERY environment: the roles its own
    /// gates name, one organisation for users to belong to, and an administrator to sign in as.
    /// </summary>
    public async Task ProvisionAsync()
    {
        try
        {
            await EnsureRolesAsync();
            await EnsureDefaultTenantAsync();
            await EnsureAdministratorAsync();
            _context.ChangeTracker.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while provisioning the database");
            throw;
        }
    }

    /// <summary>
    /// Material that exists only to make a development environment pleasant to work in. Never runs
    /// outside Development - a production database should start empty of examples.
    /// </summary>
    public async Task SeedSampleDataAsync()
    {
        try
        {
            await SeedSampleTenantAsync();
            await SeedPicklistsAsync();
            _context.ChangeTracker.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while seeding sample data");
            throw;
        }
    }

    /// <summary>
    /// The claims granted to the <see cref="Roles.Basic"/> role. Documents is the only demonstrable
    /// feature a plain user is meant to reach, so the grant is exactly what the Documents grid and
    /// its file download are gated on - <c>DocumentsWithPaginationQuery</c> requires View and
    /// <c>GetFileStreamQuery</c> requires Download. Search/Export/Import are deliberately absent:
    /// nothing enforces them server-side today, and granting unenforced claims would misrepresent
    /// what the role can actually do.
    /// </summary>
    private static readonly string[] BasicPermissions =
    [
        Permissions.Documents.View,
        Permissions.Documents.Download
    ];

    /// <summary>
    /// The name of the organisation created when a database has none. Deliberately generic: it is
    /// provisioning, not sample data, and naming it after a place would be a guess about the
    /// deployment.
    /// </summary>
    private const string DefaultTenantName = "Default";

    private async Task EnsureRolesAsync()
    {
        if (await _roleManager.RoleExistsAsync(Roles.Admin)) return;

        _logger.LogInformation("Provisioning roles...");

        var administratorRole = new ApplicationRole(Roles.Admin)
        {
            Description = "Full access to every feature and every setting.",
            CreatedAt = DateTime.UtcNow
        };
        var basicRole = new ApplicationRole(Roles.Basic)
        {
            Description = "Ordinary member: can see and download documents.",
            CreatedAt = DateTime.UtcNow
        };

        await _roleManager.CreateAsync(administratorRole);
        await _roleManager.CreateAsync(basicRole);

        // The administrator grant is an explicit list checked against the Permissions constants at
        // startup, not a reflection sweep - see AdministratorPermissionRegistry for why.
        foreach (var permission in AdministratorPermissionRegistry.Granted)
        {
            await _roleManager.AddClaimAsync(
                administratorRole, new Claim(ApplicationClaimTypes.Permission, permission));
        }

        foreach (var permission in BasicPermissions)
        {
            await _roleManager.AddClaimAsync(
                basicRole, new Claim(ApplicationClaimTypes.Permission, permission));
        }
    }

    private async Task EnsureDefaultTenantAsync()
    {
        if (await _context.Tenants.AnyAsync()) return;

        _logger.LogInformation("Provisioning the default organisation...");
        _context.Tenants.Add(new Tenant
        {
            Name = DefaultTenantName,
            Description = "Created automatically because the application needs at least one organisation."
        });
        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// Creates an administrator when nobody holds the administrator role.
    /// <para>
    /// The test is role membership, not a username: an installation whose administrator was renamed,
    /// or which has several, must not have a second one silently provisioned underneath it.
    /// </para>
    /// </summary>
    private async Task EnsureAdministratorAsync()
    {
        var existing = await _userManager.GetUsersInRoleAsync(Roles.Admin);
        if (existing.Count > 0) return;

        var tenant = await _context.Tenants.FirstAsync();
        var password = GenerateCompliantPassword();

        var administrator = new ApplicationUser
        {
            UserName = Users.Administrator,
            Provider = "Local",
            IsActive = true,
            TenantId = tenant.Id,
            DisplayName = Users.Administrator,
            Email = "administrator@localhost",
            EmailConfirmed = true,
            LanguageCode = "en-US",
            TimeZoneId = TimeZoneInfo.Utc.Id,
            TwoFactorEnabled = false,
            MustChangePassword = true,
            CreatedAt = DateTime.UtcNow,
            TenantUsers = [new TenantUser { TenantId = tenant.Id }]
        };

        var created = await _userManager.CreateAsync(administrator, password);
        if (!created.Succeeded)
        {
            throw new InvalidOperationException(
                "Could not provision the administrator account: " +
                string.Join("; ", created.Errors.Select(e => e.Description)));
        }

        await _userManager.AddToRoleAsync(administrator, Roles.Admin);

        // The only time this password is ever legible. It is not written to configuration, not to a
        // file, and not returned anywhere - the Identity password hash is the only copy that
        // survives this method.
        // The scope marks this event so the file and database sinks drop it - the console is
        // the only place the password is ever written. Serilog surfaces scope state as event
        // properties because Enrich.FromLogContext() is configured; SerilogExtensions.CarriesBootstrapSecret
        // is the matching half.
        using var secretScope = _logger.BeginScope(new Dictionary<string, object>
        {
            [SerilogExtensions.BootstrapSecretProperty] = true
        });

        // The :l format matters - without it Serilog renders string properties in quotes, which
        // would make the operator copy a password with quotation marks around it.
        _logger.LogWarning(
            "\n================ ADMINISTRATOR ACCOUNT CREATED ================\n" +
            "  Username: {UserName:l}\n" +
            "  Password: {Password:l}\n" +
            "This password was generated for this installation and is shown ONCE, here, now.\n" +
            "It cannot be read back from the application. Copy it before this process exits.\n" +
            "You will be required to change it the first time you sign in.\n" +
            "===============================================================",
            administrator.UserName, password);
    }

    /// <summary>
    /// A password that satisfies the configured Identity policy by construction rather than by
    /// retrying until one happens to pass.
    /// <para>
    /// Drawn from <see cref="RandomNumberGenerator"/>, never <c>System.Random</c>: this value is a
    /// credential, and <c>Random</c> is seeded predictably enough that a generated administrator
    /// password would be guessable from the process start time.
    /// </para>
    /// </summary>
    private string GenerateCompliantPassword()
    {
        const string lower = "abcdefghijkmnopqrstuvwxyz";     // no l
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";      // no I, O
        const string digits = "23456789";                     // no 0, 1
        const string symbols = "!@#$%^&*-_=+?";

        var policy = _identityOptions.Password;
        var required = new List<char>();

        if (policy.RequireLowercase) required.Add(Pick(lower));
        if (policy.RequireUppercase) required.Add(Pick(upper));
        if (policy.RequireDigit) required.Add(Pick(digits));
        if (policy.RequireNonAlphanumeric) required.Add(Pick(symbols));

        // The pool always spans all four classes so RequiredUniqueChars is reachable whatever the
        // policy demands of the mandatory prefix.
        var pool = lower + upper + digits + symbols;

        // Comfortably above any sane RequiredLength, and under the 30-character cap the sign-in form
        // imposes on the field this will be typed into.
        var length = Math.Clamp(Math.Max(policy.RequiredLength, 20), required.Count, 24);

        var characters = new List<char>(required);
        while (characters.Count < length) characters.Add(Pick(pool));

        // Fisher-Yates over the same cryptographic source, so the mandatory characters do not sit in
        // a fixed, predictable order at the front.
        for (var i = characters.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (characters[i], characters[j]) = (characters[j], characters[i]);
        }

        return new string(characters.ToArray());

        static char Pick(string set) => set[RandomNumberGenerator.GetInt32(set.Length)];
    }

    private async Task SeedSampleTenantAsync()
    {
        const string sampleTenantName = "Europe";
        if (await _context.Tenants.AnyAsync(t => t.Name == sampleTenantName)) return;

        _logger.LogInformation("Seeding a second organisation...");
        var tenant = new Tenant { Name = sampleTenantName, Description = "Europe Site" };
        _context.Tenants.Add(tenant);
        await _context.SaveChangesAsync();

        // Keep the development administrator a member of every organisation, which is what makes
        // tenant switching demonstrable at all.
        var administrators = await _userManager.GetUsersInRoleAsync(Roles.Admin);
        foreach (var administrator in administrators)
        {
            if (await _context.TenantUsers.AnyAsync(tu => tu.UserId == administrator.Id && tu.TenantId == tenant.Id))
                continue;

            _context.TenantUsers.Add(new TenantUser { UserId = administrator.Id, TenantId = tenant.Id });
        }

        await _context.SaveChangesAsync();
    }

    private async Task SeedPicklistsAsync()
    {
        if (await _context.PicklistSets.AnyAsync()) return;

        _logger.LogInformation("Seeding picklist values...");
        var keyValues = new[]
        {
            new PicklistSet
            {
                Name = Picklist.Status,
                Value = "initialization",
                Text = "Initialization",
                Description = "Status of workflow"
            },
            new PicklistSet
            {
                Name = Picklist.Status,
                Value = "processing",
                Text = "Processing",
                Description = "Status of workflow"
            },
            new PicklistSet
            {
                Name = Picklist.Status,
                Value = "pending",
                Text = "Pending",
                Description = "Status of workflow"
            },
            new PicklistSet
            {
                Name = Picklist.Status,
                Value = "done",
                Text = "Done",
                Description = "Status of workflow"
            },
            new PicklistSet
            {
                Name = Picklist.Brand,
                Value = "Apple",
                Text = "Apple",
                Description = "Brand of production"
            },
            new PicklistSet
            {
                Name = Picklist.Brand,
                Value = "Google",
                Text = "Google",
                Description = "Brand of production"
            },
            new PicklistSet
            {
                Name = Picklist.Brand,
                Value = "Microsoft",
                Text = "Microsoft",
                Description = "Brand of production"
            },
            new PicklistSet
            {
                Name = Picklist.Unit,
                Value = "EA",
                Text = "EA",
                Description = "Unit of measure"
            },
            new PicklistSet
            {
                Name = Picklist.Unit,
                Value = "KM",
                Text = "KM",
                Description = "Unit of measure"
            },
            new PicklistSet
            {
                Name = Picklist.Unit,
                Value = "PC",
                Text = "PC",
                Description = "Unit of measure"
            },
            new PicklistSet
            {
                Name = Picklist.Unit,
                Value = "L",
                Text = "L",
                Description = "Unit of measure"
            }
        };

        await _context.PicklistSets.AddRangeAsync(keyValues);
        await _context.SaveChangesAsync();
    }
}
