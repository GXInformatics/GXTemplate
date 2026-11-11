using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Approvals;
using CleanArchitecture.Blazor.Application.Common.Constants;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Transactions;
using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Application.IntegrationTests.DataFoundations;
using CleanArchitecture.Blazor.Domain.Identity;
using CleanArchitecture.Blazor.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using static CleanArchitecture.Blazor.Application.IntegrationTests.Testing;

namespace CleanArchitecture.Blazor.Application.IntegrationTests.Extensions;

/// <summary>
/// Pass 60b plan §5 rows 6 to 8: the role catalogue reconciled by ProvisionAsync (item 3), and an administrator who
/// holds an approval permission logged, not fatal (decision 8).
/// </summary>
public class RoleCatalogueTests : TestBase
{
    /// <summary>Records what the initializer logs.</summary>
    private sealed class ListLogger : ILogger<ApplicationDbContextInitializer>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private static async Task<ListLogger> ProvisionAsync(IEnumerable<ApplicationRoleDefinition> definitions,
        IEnumerable<ApplicationRoleGrant> grants, RoleManager<ApplicationRole>? roleManager = null)
    {
        using var scope = CreateScope();
        var logger = new ListLogger();
        var extra = new List<object> { logger, definitions.ToArray(), grants.ToArray() };
        if (roleManager is not null) extra.Add(roleManager);
        var initializer = ActivatorUtilities.CreateInstance<ApplicationDbContextInitializer>(scope.ServiceProvider, extra.ToArray());
        await initializer.ProvisionAsync();
        return logger;
    }

    private static async Task<string[]> ClaimsOfAsync(string role)
    {
        using var scope = CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        var found = await roleManager.FindByNameAsync(role);
        found.Should().NotBeNull($"role {role} exists");
        return (await roleManager.GetClaimsAsync(found!)).Where(c => c.Type == ApplicationClaimTypes.Permission).Select(c => c.Value).ToArray();
    }

    private static string Name(string stem) => stem + " " + Guid.NewGuid().ToString("N")[..6];

    [Test]
    public async Task T06_ACatalogueRole_IsCreated_GrantsAreAdded_AndNothingIsEverRevokedOrOverwritten()
    {
        var storekeeper = Name("Storekeeper");
        var definition = new ApplicationRoleDefinition(storekeeper, "Receives and issues stock.", [Permissions.Documents.View], "Inventory");

        var first = await ProvisionAsync([definition], []);
        (await ClaimsOfAsync(storekeeper)).Should().BeEquivalentTo(Permissions.Documents.View);
        first.Entries.Should().Contain(e => e.Message.Contains($"Provisioned the {storekeeper} role"));

        // An operator adds a claim and edits the description.
        using (var scope = CreateScope())
        {
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            var role = (await roleManager.FindByNameAsync(storekeeper))!;
            await roleManager.AddClaimAsync(role, new Claim(ApplicationClaimTypes.Permission, Permissions.Documents.Create));
            role.Description = "Edited by an operator.";
            await roleManager.UpdateAsync(role);
        }

        // A later release adds a grant from another module, and drops View from the declaration.
        var later = await ProvisionAsync([definition with { Permissions = [] }],
            [new ApplicationRoleGrant(storekeeper, [Permissions.Documents.Download], "Sales")]);

        (await ClaimsOfAsync(storekeeper)).Should().BeEquivalentTo(
            Permissions.Documents.View, Permissions.Documents.Create, Permissions.Documents.Download);
        later.Entries.Should().Contain(e => e.Message.Contains($"Granted 1 permission(s) to the {storekeeper} role"));

        using (var scope = CreateScope())
        {
            var role = await scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>().FindByNameAsync(storekeeper);
            role!.Description.Should().Be("Edited by an operator.");
        }

        // A start that changes nothing says nothing about the role.
        (await ProvisionAsync([definition], [new ApplicationRoleGrant(storekeeper, [Permissions.Documents.Download], "Sales")]))
            .Entries.Should().NotContain(e => e.Message.Contains(storekeeper));
    }

    [Test]
    public async Task T06_BasicReceivesACatalogueGrant()
    {
        await ProvisionAsync([], [new ApplicationRoleGrant(Roles.Basic, [Permissions.Documents.Create], "AppLevel")]);
        (await ClaimsOfAsync(Roles.Basic)).Should().Contain(Permissions.Documents.Create);

        // Grant-only means the reconcile never takes it back; this test does, so the shared Basic role is as it was.
        using var scope = CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        await roleManager.RemoveClaimAsync((await roleManager.FindByNameAsync(Roles.Basic))!,
            new Claim(ApplicationClaimTypes.Permission, Permissions.Documents.Create));
    }

    [Test]
    public async Task T07_AnInvalidCatalogue_StopsProvisioning_BeforeAnyCatalogueRoleIsWritten()
    {
        var picker = Name("Picker");
        var act = () => ProvisionAsync([new ApplicationRoleDefinition(picker, "Picks.", [Permissions.Documents.View], "Inventory")],
            [new ApplicationRoleGrant("Nobody Declares Me", [Permissions.Documents.View], "Sales")]);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*which no module declares*");
        using var scope = CreateScope();
        (await scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>().FindByNameAsync(picker)).Should().BeNull();
    }

    private sealed class RefusingRoleValidator : IRoleValidator<ApplicationRole>
    {
        public Task<IdentityResult> ValidateAsync(RoleManager<ApplicationRole> manager, ApplicationRole role) =>
            Task.FromResult(role.Name!.StartsWith("Refused", StringComparison.Ordinal)
                ? IdentityResult.Failed(new IdentityError { Code = "Refused", Description = "refused by the test validator" })
                : IdentityResult.Success);
    }

    [Test]
    public async Task T07_ACatalogueRoleIdentityRefusesToCreate_StopsStartup()
    {
        using var scope = CreateScope();
        var sp = scope.ServiceProvider;
        var refusing = new RoleManager<ApplicationRole>(sp.GetRequiredService<IRoleStore<ApplicationRole>>(),
            [new RefusingRoleValidator()], sp.GetRequiredService<ILookupNormalizer>(), new IdentityErrorDescriber(),
            sp.GetRequiredService<ILogger<RoleManager<ApplicationRole>>>());

        var refused = Name("Refused");
        var act = () => ProvisionAsync([new ApplicationRoleDefinition(refused, "Never created.", [], "Inventory")], [], refusing);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"Could not provision the catalogue role {refused}: refused by the test validator");
    }

    [Test]
    public async Task T08_AnAdministratorHoldingAnApprovalPermission_IsLoggedOnEveryStart_StartupContinues_AndTheirApprovalIsRefused()
    {
        var administrator = await RunAsAdministratorAsync();
        var holder = Name("Budget Holder");

        // A catalogue role that an administrator already belongs to; a later release gives it an approve permission.
        await ProvisionAsync([new ApplicationRoleDefinition(holder, "Approves budgets.", [Permissions.Documents.View], "Accounting")], []);
        using (var scope = CreateScope())
        {
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            await roleManager.AddClaimAsync((await roleManager.FindByNameAsync(holder))!,
                new Claim(ApplicationClaimTypes.Permission, TestApprovalPermissions.Approve));
            (await userManager.AddToRoleAsync((await userManager.FindByIdAsync(administrator))!, holder)).Succeeded.Should().BeTrue();
        }

        var definitions = new[] { new ApplicationRoleDefinition(holder, "Approves budgets.", [Permissions.Documents.View], "Accounting") };
        for (var start = 1; start <= 2; start++)
        {
            var log = await ProvisionAsync(definitions, []); // succeeds: no exception
            log.Entries.Where(e => e.Level == LogLevel.Error).Should().ContainSingle($"start {start} logs it once")
                .Which.Message.Should().Contain("administrator").And.Contain(administrator)
                .And.Contain(TestApprovalPermissions.Approve).And.Contain(holder);
        }

        // The guarantee is unchanged: the policy refuses the administrator at send time.
        using (var scope = CreateScope())
        {
            await using var session = await scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>().BeginAsync(CancellationToken.None);
            var decision = await scope.ServiceProvider.GetRequiredService<IApprovalPolicy>().DecideAsync(
                new TestApprovableDocument { ApprovalValue = 1m }, TestApprovableDocument.ApprovalDocumentType, administrator, null, CancellationToken.None);
            decision.Refusal.Should().Be(ApprovalRefusal.Administrator);
        }

        // Without the administrator in the role, nothing is logged.
        using (var scope = CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            await userManager.RemoveFromRoleAsync((await userManager.FindByIdAsync(administrator))!, holder);
        }
        (await ProvisionAsync(definitions, [])).Entries.Should().NotContain(e => e.Level == LogLevel.Error);
    }
}
