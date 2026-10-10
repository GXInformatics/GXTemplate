using System;
using System.Linq;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.ExceptionHandlers;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Features.PicklistSets.Queries.GetAll;
using CleanArchitecture.Blazor.Domain.Entities;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.IntegrationTests.MultiTenant;

using static Testing;

/// <summary>
/// Pass 54: background work runs as the system account inside one tenant, through
/// <see cref="ISystemContext"/> - and is refused outside it.
/// </summary>
public class SystemContextTests : TestBase
{
    // A fresh tenant per test: GetAllPicklistSetsQuery is cached per tenant, and the cache outlives
    // the database reset between tests.
    private string _tenant = null!;
    private string _otherTenant = null!;

    [SetUp]
    public async Task ProvisionAndSeed()
    {
        using (var scope = CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContextInitializer>().ProvisionAsync();
        }

        _tenant = "t-" + Guid.NewGuid().ToString("N");
        _otherTenant = "t-" + Guid.NewGuid().ToString("N");
        await AddAsync(new Tenant { Id = _tenant, Name = _tenant });
        await AddAsync(new Tenant { Id = _otherTenant, Name = _otherTenant });
        await AddAsync(new PicklistSet { Name = Picklist.Unit, Value = "mine", Text = "mine", TenantId = _tenant });
        await AddAsync(new PicklistSet { Name = Picklist.Unit, Value = "theirs", Text = "theirs", TenantId = _otherTenant });

        // Background work: nobody is signed in.
        UseUser(null);
    }

    private static ISystemContext System() => CreateScope().ServiceProvider.GetRequiredService<ISystemContext>();

    [Test]
    public async Task OutsideTheScope_ARequestIsDenied()
    {
        var send = () => SendAsync(new GetAllPicklistSetsQuery());

        await send.Should().ThrowAsync<ForbiddenAccessException>();
    }

    [Test]
    public async Task InsideTheScope_TheSameRequestPassesAuthorization_AndReadsOnlyThatTenant()
    {
        var picklists = await System().RunAsync(_tenant, _ => SendAsync(new GetAllPicklistSetsQuery()));

        picklists.Select(p => p.Value).Should().Equal(["mine"],
            "the request was authorized as the system account and filtered to the scope's tenant");
    }

    [Test]
    public async Task AWriteInsideTheScope_IsStampedWithTheTenant_AndAttributedToTheSystemAccount()
    {
        string systemId;
        using (var scope = CreateScope())
        {
            systemId = (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>()
                .FindByNameAsync(Users.System))!.Id;
        }

        await System().RunAsync(_tenant, async ct =>
        {
            using var scope = CreateScope();
            await using var db = await scope.ServiceProvider.GetRequiredService<IApplicationDbContextFactory>().CreateAsync(ct);
            db.PicklistSets.Add(new PicklistSet { Name = Picklist.Brand, Value = "from-a-job", Text = "from-a-job" });
            await db.SaveChangesAsync(ct);
        });

        using var read = CreateScope();
        var row = await read.ServiceProvider.GetRequiredService<ApplicationDbContext>().PicklistSets
            .IgnoreQueryFilters([QueryFilters.Tenant]).SingleAsync(p => p.Value == "from-a-job");
        row.TenantId.Should().Be(_tenant);
        row.CreatedById.Should().Be(systemId, "\"who did this\" reads as the system account, not as nobody");
    }

    [Test]
    public async Task TheScopeEnds_WhenTheWorkDoes()
    {
        await System().RunAsync(_tenant, _ => Task.CompletedTask);

        CreateScope().ServiceProvider.GetRequiredService<IUserContextAccessor>().Current.Should().BeNull();
        var send = () => SendAsync(new GetAllPicklistSetsQuery());
        await send.Should().ThrowAsync<ForbiddenAccessException>();
    }

    [Test]
    public async Task AnUnknownOrMissingTenant_IsRefused_BeforeTheWorkRuns()
    {
        var ran = false;

        var unknown = () => System().RunAsync("no-such-tenant", _ => { ran = true; return Task.CompletedTask; });
        var empty = () => System().RunAsync("", _ => { ran = true; return Task.CompletedTask; });

        await unknown.Should().ThrowAsync<InvalidOperationException>().WithMessage("*does not exist*");
        await empty.Should().ThrowAsync<InvalidOperationException>();
        ran.Should().BeFalse();
    }
}
