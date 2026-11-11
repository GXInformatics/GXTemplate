using System.Collections;
using CleanArchitecture.Blazor.Domain.Common.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CleanArchitecture.Blazor.Application.Common.Modules;

/// <summary>
/// A module's own service registrations in one layer (Pass 60b, item 2). One per module per layer, in that layer's
/// <c>Modules/&lt;M&gt;/</c> folder, found by scan when the layer registers its services: a module never edits a
/// shared <c>DependencyInjection</c> file.
/// </summary>
/// <remarks>
/// <code>
/// // src/Infrastructure/Modules/Inventory/InventoryServices.cs
/// public sealed class InventoryServices : ModuleServices&lt;InventoryModule&gt;
/// {
///     public override void Register(IModuleServiceCollection services, IConfiguration configuration)
///     {
///         services.AddScoped&lt;IStockLevels, StockLevels&gt;();             // a Contracts implementation
///         services.AddScoped&lt;ITenantSeeder, InventoryTenantSeeder&gt;();  // after lower-Order modules' seeders
///     }
/// }
/// </code>
/// <para>
/// <b>Lifetimes</b> are the author's, per call: scoped for anything that touches the database, the user context
/// or Mediator; singleton only for a stateless service with singleton dependencies; transient almost never.
/// <c>ModuleServicesResolveTests</c> resolves every service a module registers under scope validation, so a
/// singleton that captures a scoped service fails a shipped test.
/// </para>
/// <para><b>Order:</b> the template's services first, then modules by <see cref="ModuleDefinition.Order"/> and name,
/// then <see cref="AppLevelServices"/>. See <see cref="ModuleServiceRegistration"/> for what fails startup.</para>
/// </remarks>
public abstract class ModuleServices<TModule> : IServiceRegistration where TModule : ModuleDefinition, new()
{
    public abstract void Register(IModuleServiceCollection services, IConfiguration configuration);
}

/// <summary>
/// The service registrations of the application's own code that belongs to no module, in one layer
/// (Pass 60b, amendment E2). At most one per layer, in that layer's <c>AppLevel/</c> folder; it runs after every
/// module. The same rules as <see cref="ModuleServices{TModule}"/> apply.
/// </summary>
public abstract class AppLevelServices : IServiceRegistration
{
    public abstract void Register(IModuleServiceCollection services, IConfiguration configuration);
}

/// <summary>What both registration kinds have in common.</summary>
internal interface IServiceRegistration
{
    void Register(IModuleServiceCollection services, IConfiguration configuration);
}

/// <summary>The service collection a module (or the application level) registers into.</summary>
public interface IModuleServiceCollection : IServiceCollection
{
    /// <summary>The module's name, or <see cref="ModuleServiceRegistration.AppLevelOwner"/>.</summary>
    string Owner { get; }

    /// <summary>
    /// The one sanctioned way to replace a service the template (or an earlier module) registered (decision 5). The
    /// replacement is logged at startup, naming the owner. Replacing a service nobody registered fails.
    /// </summary>
    IModuleServiceCollection Replace<TService, TImplementation>(ServiceLifetime lifetime)
        where TService : class where TImplementation : class, TService;
}

/// <summary>
/// What the modules registered, kept as a singleton so startup can log replacements and a test can resolve every
/// module service (Pass 60b).
/// </summary>
public sealed class ModuleServiceLedger
{
    private readonly List<(ServiceDescriptor Descriptor, string Owner)> _added = new();
    private readonly List<(Type Service, Type Implementation, string Owner)> _replaced = new();

    public IReadOnlyList<(ServiceDescriptor Descriptor, string Owner)> Added => _added;
    public IReadOnlyList<(Type Service, Type Implementation, string Owner)> Replaced => _replaced;

    /// <summary>The owners whose registrations ran, in order (module names, then <c>AppLevel</c>).</summary>
    public List<string> Owners { get; } = new();

    internal void Add(ServiceDescriptor descriptor, string owner) => _added.Add((descriptor, owner));
    internal void AddReplacement(Type service, Type implementation, string owner) => _replaced.Add((service, implementation, owner));

    internal bool IsModuleDescriptor(ServiceDescriptor descriptor) => _added.Any(a => ReferenceEquals(a.Descriptor, descriptor));

    internal bool WasReplaced(Type service, string owner) => _replaced.Any(r => r.Service == service && r.Owner == owner);
}

/// <summary>Scans a layer for module and application-level registrations and runs them under the guard (Pass 60b).</summary>
public static class ModuleServiceRegistration
{
    public const string AppLevelOwner = "AppLevel";
    public const string AppLevelSegment = "AppLevel";

    /// <summary>
    /// Runs every <see cref="ModuleServices{TModule}"/> and the <see cref="AppLevelServices"/> found in
    /// <paramref name="assembly"/>, in order. Each layer calls it for its own assembly at the end of its
    /// <c>Add…</c> method, so the order in which the layers are added does not matter.
    /// </summary>
    /// <param name="multiServices">Further service types that are registered many times (a layer's own, such as the
    /// menu contributor); see <see cref="ModuleServiceGuard.IsMultiService"/>.</param>
    /// <exception cref="InvalidOperationException">A registration broke a rule; the message lists every problem.</exception>
    public static IServiceCollection AddModuleServices(this IServiceCollection services, Assembly assembly,
        IConfiguration? configuration, params Type[] multiServices) =>
        services.AddModuleServices(assembly.GetTypes(), assembly.GetName().Name ?? "?", configuration, multiServices);

    /// <summary>The same over explicit candidate types, so tests can plant registrations.</summary>
    public static IServiceCollection AddModuleServices(this IServiceCollection services, IEnumerable<Type> types, string layer,
        IConfiguration? configuration, params Type[] multiServices)
    {
        var registrations = Find(types, layer, out var problems);
        if (problems.Count > 0) throw ModuleServiceGuard.Failure(problems);
        if (registrations.Count == 0) return services;

        var ledger = LedgerOf(services);
        configuration ??= new ConfigurationBuilder().Build();
        foreach (var registration in registrations)
        {
            var collection = new ModuleServiceCollection(services, ledger, registration.Owner);
            registration.Register(collection, configuration);
            ledger.Owners.Add(registration.Owner);
            problems.AddRange(ModuleServiceGuard.CheckOwner(services, ledger, registration.Owner, multiServices));
        }
        if (problems.Count > 0) throw ModuleServiceGuard.Failure(problems);
        return services;
    }

    /// <summary>The ledger in <paramref name="services"/>, added on first use.</summary>
    public static ModuleServiceLedger LedgerOf(IServiceCollection services)
    {
        if (services.FirstOrDefault(d => d.ServiceType == typeof(ModuleServiceLedger))?.ImplementationInstance is ModuleServiceLedger existing)
            return existing;
        var ledger = new ModuleServiceLedger();
        services.AddSingleton(ledger);
        return ledger;
    }

    private sealed record Registration(string Owner, int Order, Action<IModuleServiceCollection, IConfiguration> Register);

    /// <summary>The registrations in <paramref name="assembly"/>, in run order, with any placement problems.</summary>
    private static List<Registration> Find(IEnumerable<Type> types, string layer, out List<string> problems)
    {
        problems = new List<string>();
        var modules = new List<Registration>();
        var appLevel = new List<Registration>();

        foreach (var type in types.Where(t => t is { IsClass: true, IsAbstract: false }))
        {
            if (ModuleTypeOf(type) is { } moduleType)
            {
                var module = (ModuleDefinition)Activator.CreateInstance(moduleType)!;
                if (ModuleSegmentOf(type) != module.Name)
                    problems.Add($"{type.FullName}: registers module {module.Name}'s services but is not under Modules.{module.Name}. " +
                                 $"It belongs in {layer}'s Modules/{module.Name}/ folder.");
                var instance = (IServiceRegistration)Activator.CreateInstance(type)!;
                modules.Add(new Registration(module.Name, module.Order, instance.Register));
            }
            else if (typeof(AppLevelServices).IsAssignableFrom(type))
            {
                if (!IsAppLevel(type))
                    problems.Add($"{type.FullName}: registers application-level services but is not under an AppLevel namespace " +
                                 $"(or is inside Modules). It belongs in {layer}'s AppLevel/ folder.");
                var instance = (AppLevelServices)Activator.CreateInstance(type)!;
                appLevel.Add(new Registration(AppLevelOwner, int.MaxValue, instance.Register));
            }
        }

        foreach (var twice in modules.GroupBy(m => m.Owner).Where(g => g.Count() > 1))
            problems.Add($"Module {twice.Key} has {twice.Count()} service registrations in {layer}; one per module per layer.");
        if (appLevel.Count > 1)
            problems.Add($"{layer} has {appLevel.Count} AppLevelServices; one per layer.");

        return modules.OrderBy(m => m.Order).ThenBy(m => m.Owner, StringComparer.Ordinal).Concat(appLevel).ToList();
    }

    private static Type? ModuleTypeOf(Type type)
    {
        for (var t = type.BaseType; t is not null; t = t.BaseType)
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ModuleServices<>))
                return t.GetGenericArguments()[0];
        return null;
    }

    /// <summary>The namespace segment after <c>Modules</c>, or null.</summary>
    public static string? ModuleSegmentOf(Type type)
    {
        var segments = type.Namespace?.Split('.') ?? [];
        var index = Array.IndexOf(segments, "Modules");
        return index >= 0 && index + 1 < segments.Length ? segments[index + 1] : null;
    }

    /// <summary>True for application-level code: an <c>AppLevel</c> namespace segment and no <c>Modules</c> one.</summary>
    public static bool IsAppLevel(Type type)
    {
        var segments = type.Namespace?.Split('.') ?? [];
        return segments.Contains(AppLevelSegment) && !segments.Contains("Modules");
    }

    /// <summary>Records every descriptor a module adds and refuses removals; <see cref="Replace{TService,TImplementation}"/> is the exception.</summary>
    private sealed class ModuleServiceCollection(IServiceCollection inner, ModuleServiceLedger ledger, string owner) : IModuleServiceCollection
    {
        public string Owner => owner;

        public IModuleServiceCollection Replace<TService, TImplementation>(ServiceLifetime lifetime)
            where TService : class where TImplementation : class, TService
        {
            var existing = inner.Where(d => d.ServiceType == typeof(TService)).ToList();
            if (existing.Count == 0)
                throw ModuleServiceGuard.Failure([$"Module {owner} replaces {typeof(TService).FullName}, which nothing registered. Register it with Add… instead."]);
            foreach (var descriptor in existing) inner.Remove(descriptor);
            var replacement = new ServiceDescriptor(typeof(TService), typeof(TImplementation), lifetime);
            inner.Add(replacement);
            ledger.Add(replacement, owner);
            ledger.AddReplacement(typeof(TService), typeof(TImplementation), owner);
            return this;
        }

        public void Add(ServiceDescriptor item)
        {
            inner.Add(item);
            ledger.Add(item, owner);
        }

        public void Insert(int index, ServiceDescriptor item)
        {
            inner.Insert(index, item);
            ledger.Add(item, owner);
        }

        public ServiceDescriptor this[int index]
        {
            get => inner[index];
            set => throw Refused("overwrite a registration");
        }

        public bool Remove(ServiceDescriptor item) => throw Refused("remove a registration");
        public void RemoveAt(int index) => throw Refused("remove a registration");
        public void Clear() => throw Refused("clear the service collection");

        private InvalidOperationException Refused(string what) =>
            ModuleServiceGuard.Failure([$"Module {owner} tried to {what}. A module adds services; it replaces one only through Replace<TService, TImplementation>()."]);

        public int Count => inner.Count;
        public bool IsReadOnly => inner.IsReadOnly;
        public bool Contains(ServiceDescriptor item) => inner.Contains(item);
        public void CopyTo(ServiceDescriptor[] array, int arrayIndex) => inner.CopyTo(array, arrayIndex);
        public int IndexOf(ServiceDescriptor item) => inner.IndexOf(item);
        public IEnumerator<ServiceDescriptor> GetEnumerator() => inner.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
