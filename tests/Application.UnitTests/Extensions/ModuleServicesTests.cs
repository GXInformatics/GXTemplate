#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Interfaces.MultiTenant;
using CleanArchitecture.Blazor.Application.Common.Modules;
using CleanArchitecture.Blazor.Application.UnitTests.Extensions.Samples.AppLevel;
using CleanArchitecture.Blazor.Application.UnitTests.Extensions.Samples.Elsewhere;
using CleanArchitecture.Blazor.Application.UnitTests.Extensions.Samples.Modules.HookAlpha;
using CleanArchitecture.Blazor.Application.UnitTests.Extensions.Samples.Modules.HookAlpha.Contracts;
using CleanArchitecture.Blazor.Application.UnitTests.Extensions.Samples.Modules.HookBeta;
using CleanArchitecture.Blazor.Domain.Common.Modules;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Extensions.Samples.Modules.HookAlpha
{
    public sealed class HookAlphaModule : ModuleDefinition
    {
        public override string Name => "HookAlpha";
        public override int Order => 2;
    }

    public sealed class AlphaSeeder : ITenantSeeder { public Task SeedAsync(string tenantId, CancellationToken cancellationToken) => Task.CompletedTask; }
    public sealed class AlphaLookup : IAlphaLookup;
    public sealed class FakeStorage : IFakeTemplateService;
    public sealed class AlphaInterceptor : SaveChangesInterceptor;

    public sealed class HookAlphaServices : ModuleServices<HookAlphaModule>
    {
        public override void Register(IModuleServiceCollection services, IConfiguration configuration)
        {
            services.AddScoped<ITenantSeeder, AlphaSeeder>();
            services.AddScoped<IAlphaLookup, AlphaLookup>();
        }
    }

    public sealed class SecondAlphaServices : ModuleServices<HookAlphaModule>
    {
        public override void Register(IModuleServiceCollection services, IConfiguration configuration) { }
    }

    public sealed class SilentReplacementServices : ModuleServices<HookAlphaModule>
    {
        public override void Register(IModuleServiceCollection services, IConfiguration configuration) =>
            services.AddScoped<IFakeTemplateService, FakeStorage>();
    }

    public sealed class DeclaredReplacementServices : ModuleServices<HookAlphaModule>
    {
        public override void Register(IModuleServiceCollection services, IConfiguration configuration) =>
            services.Replace<IFakeTemplateService, FakeStorage>(ServiceLifetime.Scoped);
    }

    public sealed class InterceptorServices : ModuleServices<HookAlphaModule>
    {
        public override void Register(IModuleServiceCollection services, IConfiguration configuration) =>
            services.AddScoped<ISaveChangesInterceptor, AlphaInterceptor>();
    }

    public sealed class RemovingServices : ModuleServices<HookAlphaModule>
    {
        public override void Register(IModuleServiceCollection services, IConfiguration configuration) =>
            services.Remove(services.First());
    }
}

namespace CleanArchitecture.Blazor.Application.UnitTests.Extensions.Samples.Modules.HookAlpha.Contracts
{
    public interface IAlphaLookup;

    [NotAService("Implemented by other modules' rows; read by type tests, never resolved.")]
    public interface IAlphaShape;

    [NotAService("  ")]
    public interface IAlphaBlankReason;
}

namespace CleanArchitecture.Blazor.Application.UnitTests.Extensions.Samples.Modules.HookBeta
{
    public sealed class HookBetaModule : ModuleDefinition
    {
        public override string Name => "HookBeta";
        public override int Order => 1;
    }

    public sealed class BetaSeeder : ITenantSeeder { public Task SeedAsync(string tenantId, CancellationToken cancellationToken) => Task.CompletedTask; }

    public sealed class HookBetaServices : ModuleServices<HookBetaModule>
    {
        public override void Register(IModuleServiceCollection services, IConfiguration configuration) =>
            services.AddScoped<ITenantSeeder, BetaSeeder>();
    }

    /// <summary>Registers module Alpha's services from module Beta's folder: rule 1.</summary>
    public sealed class MisplacedAlphaServices : ModuleServices<HookAlphaModule>
    {
        public override void Register(IModuleServiceCollection services, IConfiguration configuration) { }
    }
}

namespace CleanArchitecture.Blazor.Application.UnitTests.Extensions.Samples.AppLevel
{
    public sealed class AppSeeder : ITenantSeeder { public Task SeedAsync(string tenantId, CancellationToken cancellationToken) => Task.CompletedTask; }
    public sealed class AppClock;

    public sealed class AppServices : AppLevelServices
    {
        public override void Register(IModuleServiceCollection services, IConfiguration configuration)
        {
            services.AddScoped<ITenantSeeder, AppSeeder>();
            services.AddSingleton<AppClock>();
        }
    }

    public sealed class SecondAppServices : AppLevelServices
    {
        public override void Register(IModuleServiceCollection services, IConfiguration configuration) { }
    }
}

namespace CleanArchitecture.Blazor.Application.UnitTests.Extensions.Samples.Elsewhere
{
    /// <summary>Application-level registrations outside an AppLevel namespace.</summary>
    public sealed class StrayAppServices : AppLevelServices
    {
        public override void Register(IModuleServiceCollection services, IConfiguration configuration) { }
    }
}

namespace CleanArchitecture.Blazor.Application.UnitTests.Extensions
{
    public interface IFakeTemplateService;
    public sealed class TemplateFake : IFakeTemplateService;

    /// <summary>Pass 60b plan §5 rows 3, 4, 18 and 21: the module service hook and its guard.</summary>
    public class ModuleServicesTests
    {
        private static IServiceCollection Run(params Type[] types)
        {
            var services = new ServiceCollection();
            services.AddScoped<IFakeTemplateService, TemplateFake>(); // stands for a template registration
            services.AddModuleServices(types, "TestLayer", null);
            return services;
        }

        private static Action Running(params Type[] types) => () => Run(types);

        [Test]
        public void T03_ModulesRegisterInOrder_ThenAppLevel_AndEveryServiceResolves()
        {
            var services = Run(typeof(HookAlphaServices), typeof(AppServices), typeof(HookBetaServices));

            // Beta (Order 1) before Alpha (Order 2), and the application level last.
            ModuleServiceRegistration.LedgerOf(services).Owners.Should().Equal("HookBeta", "HookAlpha", "AppLevel");
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
            using var scope = provider.CreateScope();
            scope.ServiceProvider.GetServices<ITenantSeeder>().Select(s => s.GetType())
                .Should().Equal(typeof(BetaSeeder), typeof(AlphaSeeder), typeof(AppSeeder));
            scope.ServiceProvider.GetRequiredService<IAlphaLookup>().Should().BeOfType<AlphaLookup>();
            scope.ServiceProvider.GetRequiredService<AppClock>().Should().NotBeNull();
        }

        [Test]
        public void T04_ARegistrationOutsideItsModulesFolder_Fails()
        {
            Running(typeof(MisplacedAlphaServices)).Should().Throw<InvalidOperationException>()
                .WithMessage("*MisplacedAlphaServices*registers module HookAlpha's services but is not under Modules.HookAlpha*");
        }

        [Test]
        public void T04_TwoRegistrationsForOneModuleInOneLayer_Fail()
        {
            Running(typeof(HookAlphaServices), typeof(SecondAlphaServices)).Should().Throw<InvalidOperationException>()
                .WithMessage("*Module HookAlpha has 2 service registrations in TestLayer*");
        }

        [Test]
        public void T04_ASilentReplacementOfATemplateService_Fails_AndADeclaredOneIsRecorded()
        {
            Running(typeof(SilentReplacementServices)).Should().Throw<InvalidOperationException>()
                .WithMessage("*HookAlpha: registers*IFakeTemplateService, which the template already registered*Replace*");

            var services = Run(typeof(DeclaredReplacementServices));
            services.Where(d => d.ServiceType == typeof(IFakeTemplateService)).Should().ContainSingle()
                .Which.ImplementationType.Should().Be(typeof(FakeStorage));
            ModuleServiceRegistration.LedgerOf(services).Replaced.Should().ContainSingle()
                .Which.Should().Be((typeof(IFakeTemplateService), typeof(FakeStorage), "HookAlpha"));
        }

        [Test]
        public void T04_AModuleInterceptor_Fails_AndAModuleCannotRemoveARegistration()
        {
            Running(typeof(InterceptorServices)).Should().Throw<InvalidOperationException>()
                .WithMessage("*HookAlpha: registers an EF interceptor*AlphaInterceptor*");
            Running(typeof(RemovingServices)).Should().Throw<InvalidOperationException>()
                .WithMessage("*Module HookAlpha tried to remove a registration*");
        }

        [Test]
        public void T18_TheContractsCheck_FailsUnregistered_PassesMarked_AndFailsMarkedButRegistered()
        {
            var services = new ServiceCollection();

            // Unmarked and unregistered: named.
            ModuleServiceGuard.ContractProblems(services, [typeof(IAlphaLookup)]).Should().ContainSingle()
                .Which.Should().Contain(typeof(IAlphaLookup).FullName).And.Contain("nothing registers it");

            // Marked as not a service: passes.
            ModuleServiceGuard.ContractProblems(services, [typeof(IAlphaShape)]).Should().BeEmpty();

            // Marked, yet registered: the marking is wrong.
            services.AddSingleton(typeof(IAlphaShape), new object());
            ModuleServiceGuard.ContractProblems(services, [typeof(IAlphaShape)]).Should().ContainSingle()
                .Which.Should().Contain("is marked [NotAService] but is registered 1 time(s)");

            // A blank reason.
            ModuleServiceGuard.ContractProblems(new ServiceCollection(), [typeof(IAlphaBlankReason)]).Should().ContainSingle()
                .Which.Should().Contain("needs a reason");

            // Registered once: passes; twice: fails.
            var once = new ServiceCollection().AddScoped<IAlphaLookup, AlphaLookup>();
            ModuleServiceGuard.ContractProblems(once, [typeof(IAlphaLookup)]).Should().BeEmpty();
            once.AddScoped<IAlphaLookup, AlphaLookup>();
            ModuleServiceGuard.ContractProblems(once, [typeof(IAlphaLookup)]).Should().ContainSingle().Which.Should().Contain("registered 2 times");

            // AssertContractsRegistered throws with the same text.
            FluentActions.Invoking(() => ModuleServiceGuard.AssertContractsRegistered(new ServiceCollection(), typeof(ModuleServicesTests).Assembly))
                .Should().Throw<InvalidOperationException>().WithMessage("*IAlphaLookup*nothing registers it*");
        }

        [Test]
        public void T18_TheTemplateItself_HasNoContractsInterface()
        {
            ModuleServiceGuard.ContractProblems(new ServiceCollection(),
                typeof(CleanArchitecture.Blazor.Domain.Common.Modules.ModuleDefinition).Assembly.GetTypes()
                    .Concat(typeof(CleanArchitecture.Blazor.Application.DependencyInjection).Assembly.GetTypes())
                    .Where(t => !(t.Namespace ?? "").Contains(".Modules.", StringComparison.Ordinal)))
                .Should().BeEmpty();
        }

        [Test]
        public void T21_AppLevelServices_OutsideAppLevel_AndTwoInOneLayer_Fail()
        {
            Running(typeof(StrayAppServices)).Should().Throw<InvalidOperationException>()
                .WithMessage("*StrayAppServices*not under an AppLevel namespace*");
            Running(typeof(AppServices), typeof(SecondAppServices)).Should().Throw<InvalidOperationException>()
                .WithMessage("*TestLayer has 2 AppLevelServices; one per layer*");
        }
    }
}
