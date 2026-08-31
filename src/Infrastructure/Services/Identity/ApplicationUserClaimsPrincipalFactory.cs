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
/// The cost of that choice is staleness: the claim only changes when the cookie is reissued, and
/// nothing a Blazor circuit does can reissue it - <c>SignInManager.RefreshSignInAsync</c> writes a
/// Set-Cookie, and by the time a component's event handler runs the response has already started.
/// So clearing the flag on the user record is not enough on its own; something has to make a real
/// HTTP request that rebuilds the ticket.
/// </para>
/// <para>
/// That is what <c>/pages/authentication/refresh-signin</c> is for, and why the change-password page
/// leaves through it rather than reloading "/" directly. Until Pass 17 it reloaded "/", the stale
/// claim survived, and a user who had just chosen a new password was sent back to the
/// change-password page - or, once the security-stamp validation interval elapsed, signed out.
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
