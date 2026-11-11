using CleanArchitecture.Blazor.Application.Common.Security;
using CleanArchitecture.Blazor.Domain.Common.Modules;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CleanArchitecture.Blazor.Application.Common.Modules;

/// <summary>
/// The rules a module's (or the application level's) service registrations must keep (Pass 60b, item 2). Each
/// broken rule fails startup with a message naming the owner and the service.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item><description>A registration class sits in its own module's namespace (or in <c>AppLevel</c>).</description></item>
/// <item><description>One registration class per module per layer, and one <c>AppLevelServices</c> per layer.</description></item>
/// <item><description>No silent replacement: a service type the template or another owner already registered may be
/// added again only if it is meant to be registered many times (<see cref="IsMultiService"/>), or through
/// <see cref="IModuleServiceCollection.Replace{TService,TImplementation}"/>.</description></item>
/// <item><description>No EF interceptor: the template's two <c>ISaveChangesInterceptor</c>s are the whole set.</description></item>
/// <item><description>Every <c>Contracts</c> interface is registered exactly once, unless it is
/// <see cref="NotAServiceAttribute"/> (amendment E1), in which case it must not be registered at all.</description></item>
/// </list>
/// Rules 1 and 2 are checked in <see cref="ModuleServiceRegistration"/>; 3 and 4 here, after each owner; 5 after
/// every layer, by <see cref="AssertContractsRegistered"/>.
/// </remarks>
public static class ModuleServiceGuard
{
    private static readonly Type[] OpenMultiServices =
    [
        typeof(IPostingEventHandler<>), typeof(INotificationHandler<>), typeof(IValidator<>),
        typeof(IConfigureOptions<>), typeof(IPostConfigureOptions<>), typeof(IValidateOptions<>)
    ];

    private static readonly Type[] ClosedMultiServices =
    [
        typeof(CleanArchitecture.Blazor.Application.Common.Interfaces.MultiTenant.ITenantSeeder),
        typeof(ApplicationRoleDefinition), typeof(ApplicationRoleGrant)
    ];

    /// <summary>
    /// True for a service type that is meant to be registered many times and resolved as a set: seeders, event and
    /// posting handlers, validators, options configuration, hosted services, role catalogue entries, and any
    /// <paramref name="extra"/> type a layer names (the menu contributor).
    /// </summary>
    public static bool IsMultiService(Type serviceType, IReadOnlyCollection<Type>? extra = null)
    {
        if (ClosedMultiServices.Contains(serviceType) || (extra?.Contains(serviceType) ?? false)) return true;
        if (serviceType.FullName == "Microsoft.Extensions.Hosting.IHostedService") return true;
        return serviceType.IsGenericType && OpenMultiServices.Contains(serviceType.GetGenericTypeDefinition());
    }

    /// <summary>Rules 3 and 4 over what <paramref name="owner"/> just added.</summary>
    internal static IEnumerable<string> CheckOwner(IServiceCollection services, ModuleServiceLedger ledger, string owner,
        IReadOnlyCollection<Type> multiServices)
    {
        foreach (var (descriptor, _) in ledger.Added.Where(a => a.Owner == owner))
        {
            var service = descriptor.ServiceType;
            var implementation = descriptor.IsKeyedService ? descriptor.KeyedImplementationType : descriptor.ImplementationType;

            if (typeof(IInterceptor).IsAssignableFrom(service) || (implementation is not null && typeof(IInterceptor).IsAssignableFrom(implementation)))
            {
                yield return $"{owner}: registers an EF interceptor ({implementation?.FullName ?? service.FullName}). The template's two " +
                             "save interceptors are the whole set; a new save rule goes in ApplicationDbContext.SaveChanges (DataRules).";
                continue;
            }

            if (IsMultiService(service, multiServices) || ledger.WasReplaced(service, owner)) continue;

            var others = services.Where(d => d.ServiceType == service && !ReferenceEquals(d, descriptor)
                                             && !ledger.Added.Any(a => ReferenceEquals(a.Descriptor, d) && a.Owner == owner)).ToList();
            if (others.Count == 0) continue;

            var by = others.Select(d => ledger.Added.FirstOrDefault(a => ReferenceEquals(a.Descriptor, d)).Owner ?? "the template").Distinct();
            yield return $"{owner}: registers {service.FullName}, which {string.Join(" and ", by)} already registered. The last " +
                         "registration would silently win. Use services.Replace<TService, TImplementation>() if replacing it is intended.";
        }
    }

    /// <summary>Rule 5 (and amendment E1), after every layer has registered.</summary>
    /// <exception cref="InvalidOperationException">An interface is unregistered, registered twice, or wrongly marked.</exception>
    public static void AssertContractsRegistered(IServiceCollection services, params Assembly[] assemblies)
    {
        var problems = ContractProblems(services, assemblies.Distinct().SelectMany(a => a.GetTypes()));
        if (problems.Count > 0) throw Failure(problems);
    }

    /// <summary>Rule 5 over explicit types, so tests can plant interfaces.</summary>
    public static IReadOnlyList<string> ContractProblems(IServiceCollection services, IEnumerable<Type> types)
    {
        var problems = new List<string>();
        foreach (var contract in types.Where(IsContractInterface))
        {
            var registrations = services.Count(d => d.ServiceType == contract
                                                    || (contract.IsGenericTypeDefinition && d.ServiceType.IsGenericType
                                                        && d.ServiceType.GetGenericTypeDefinition() == contract));
            var marker = contract.GetCustomAttribute<NotAServiceAttribute>();
            if (marker is not null)
            {
                if (string.IsNullOrWhiteSpace(marker.Reason))
                    problems.Add($"{contract.FullName}: [NotAService] needs a reason.");
                if (registrations > 0)
                    problems.Add($"{contract.FullName}: is marked [NotAService] but is registered {registrations} time(s). " +
                                 "Remove the registration, or the marking, whichever is wrong.");
                continue;
            }

            if (registrations == 0)
                problems.Add($"{contract.FullName}: is a Contracts interface and nothing registers it. Register its implementation " +
                             "in the module's ModuleServices, or mark it [NotAService(\"reason\")] if it is never resolved.");
            else if (registrations > 1 && !contract.IsGenericTypeDefinition)
                problems.Add($"{contract.FullName}: is registered {registrations} times; a Contracts service is registered exactly once.");
        }
        return problems;
    }

    /// <summary>A public interface in a <c>…Modules.&lt;M&gt;.Contracts</c> namespace.</summary>
    public static bool IsContractInterface(Type type)
    {
        if (!type.IsInterface || !(type.IsPublic || type.IsNestedPublic)) return false;
        var segments = type.Namespace?.Split('.') ?? [];
        var index = Array.IndexOf(segments, "Modules");
        return index >= 0 && index + 2 < segments.Length && segments[index + 2] == "Contracts";
    }

    internal static InvalidOperationException Failure(IEnumerable<string> problems) =>
        new("Module service registration (README, \"Modules and schemas\"):\n- " + string.Join("\n- ", problems));
}
