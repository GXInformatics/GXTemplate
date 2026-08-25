using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Domain.Identity;
using Microsoft.Extensions.Options;

namespace CleanArchitecture.Blazor.Infrastructure.Services.Identity;

/// <summary>
/// Projects <see cref="ApplicationUser.MustChangePassword"/> onto the signed-in principal as a
/// claim.
/// <para>
/// Enforcement has to happen on every request and on every in-circuit navigation. Reading a database
/// column at each of those would be a round-trip per page view for a flag that is false for
/// essentially every user, essentially always; a claim baked into the authentication cookie costs
/// nothing to read.
/// </para>
/// <para>
/// The cost of that choice is staleness: the claim only changes when the cookie is reissued. That is
/// why the change-password flow calls <c>SignInManager.RefreshSignInAsync</c> immediately after
/// clearing the flag rather than waiting for the security-stamp validation interval to come round.
/// </para>
/// </summary>
public class ApplicationUserClaimsPrincipalFactory
    : UserClaimsPrincipalFactory<ApplicationUser, ApplicationRole>
{
    public ApplicationUserClaimsPrincipalFactory(
        UserManager<ApplicationUser> userManager,
        RoleManager<ApplicationRole> roleManager,
        IOptions<IdentityOptions> options)
        : base(userManager, roleManager, options)
    {
    }

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);

        if (user.MustChangePassword)
        {
            identity.AddClaim(new Claim(ApplicationClaimTypes.MustChangePassword, "true"));
        }

        return identity;
    }
}
