using System;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.ExceptionHandlers;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Application.Features.Tenants.Commands.Create;
using CleanArchitecture.Blazor.Application.Features.Tenants.Commands.Update;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Domain.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.IntegrationTests.MultiTenant;

using static Testing;

/// <summary>
/// Pass 54: <c>AddEditTenantCommand</c> became <see cref="CreateTenantCommand"/> and
/// <see cref="UpdateTenantCommand"/>, each requiring exactly its own right.
/// </summary>
public class TenantCommandTests : TestBase
{
    private static string[] PoliciesOf(Type command) =>
        command.GetCustomAttributes<RequestAuthorizeAttribute>().Select(a => a.Policy).ToArray();

    [Test]
    public void EachCommand_NamesExactlyItsOwnRight()
    {
        PoliciesOf(typeof(CreateTenantCommand)).Should().Equal(Permissions.Tenants.Create);
        PoliciesOf(typeof(UpdateTenantCommand)).Should().Equal(Permissions.Tenants.Edit);
    }

    /// <summary>A signed-in user holding exactly <paramref name="permission"/>.</summary>
    private static async Task UseUserHoldingOnlyAsync(string permission)
    {
        using var scope = CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = "only-" + Guid.NewGuid().ToString("N")[..8], Email = $"{Guid.NewGuid():N}@example.com" };
        (await users.CreateAsync(user)).Succeeded.Should().BeTrue();
        await users.AddClaimAsync(user, new Claim(ApplicationClaimTypes.Permission, permission));
        UseUser(user.Id);
    }

    [Test]
    public async Task AnEditOnlyOperator_CannotCreateATenant()
    {
        // The defect the split closes: the old command carried Create OR Edit, so an Edit-only caller
        // created a tenant simply by sending an id that did not exist yet.
        await UseUserHoldingOnlyAsync(Permissions.Tenants.Edit);

        var create = () => SendAsync(new CreateTenantCommand { Name = "Not yours to make" });

        await create.Should().ThrowAsync<ForbiddenAccessException>();
        (await CountAsync<Tenant>()).Should().Be(0);
    }

    [Test]
    public async Task ACreateOnlyOperator_CannotUpdateATenant()
    {
        await AddAsync(new Tenant { Id = "existing", Name = "Original" });
        await UseUserHoldingOnlyAsync(Permissions.Tenants.Create);

        var update = () => SendAsync(new UpdateTenantCommand { Id = "existing", Name = "Renamed" });

        await update.Should().ThrowAsync<ForbiddenAccessException>();
        (await FindAsync<Tenant>("existing"))!.Name.Should().Be("Original");
    }

    [Test]
    public async Task Create_RefusesAnExistingId_AndChangesNothing()
    {
        // The other half of the old fall-through: Create never updates.
        await AddAsync(new Tenant { Id = "existing", Name = "Original" });

        var result = await SendAsync(new CreateTenantCommand { Id = "existing", Name = "Overwritten?" });

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain(CreateTenantCommandHandler.AlreadyExists);
        (await FindAsync<Tenant>("existing"))!.Name.Should().Be("Original");
    }

    [Test]
    public async Task Update_RefusesAnUnknownId_AndCreatesNothing()
    {
        var result = await SendAsync(new UpdateTenantCommand { Id = "nobody", Name = "Invented" });

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain(UpdateTenantCommandHandler.NotFound);
        (await CountAsync<Tenant>()).Should().Be(0);
    }

    [Test]
    public async Task CreateThenUpdate_Works()
    {
        var created = await SendAsync(new CreateTenantCommand { Name = "First", Description = "d1" });
        var updated = await SendAsync(new UpdateTenantCommand { Id = created.Data!, Name = "Second", Description = "d2" });

        updated.Succeeded.Should().BeTrue(updated.ErrorMessage);
        var tenant = (await FindAsync<Tenant>(created.Data!))!;
        tenant.Name.Should().Be("Second");
        tenant.Description.Should().Be("d2");
    }

    [Test]
    public void TheCreateCommandsDefaultId_IsAVersion7Guid_LikeTheEntityAndTheDto()
    {
        Guid.Parse(new CreateTenantCommand().Id).Version.Should().Be(7);
        Guid.Parse(new Tenant().Id).Version.Should().Be(7);
    }
}
