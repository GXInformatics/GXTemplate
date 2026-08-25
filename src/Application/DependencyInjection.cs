// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using CleanArchitecture.Blazor.Application.Common.Mappings;
using CleanArchitecture.Blazor.Application.Common.PublishStrategies;
using CleanArchitecture.Blazor.Application.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Mediator;

namespace CleanArchitecture.Blazor.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton(_ => MapsterConfiguration.Create());
        services.AddScoped<IObjectMapper, MapsterObjectMapper>();
        services.AddValidatorsFromAssembly(Assembly.GetExecutingAssembly());
        services.AddMediator(options =>
        {
            options.Assemblies = [typeof(CleanArchitecture.Blazor.Application.DependencyInjection), typeof(CleanArchitecture.Blazor.Domain.Common.DomainEvent)];
            options.NotificationPublisherType = typeof(ChannelBasedNoWaitPublisher);
            options.ServiceLifetime = ServiceLifetime.Scoped;
            // AuthorizationBehaviour must stay FIRST: Mediator composes behaviours last-to-first, so
            // the first entry is the outermost and nothing runs before deny-by-default has passed.
            options.PipelineBehaviors = [
                typeof(AuthorizationBehaviour<,>),
                typeof(ValidationBehavior<,>),
                typeof(ResultExceptionBehavior<,>),
                typeof(PerformanceBehaviour<,>),
                typeof(FusionCacheBehaviour<,>),
                typeof(CacheInvalidationBehaviour<,>)
                ];

        });
       
        // Deny-by-default is enforced at dispatch time by AuthorizationBehaviour; this fails the
        // application at startup instead, so an unmarked request cannot reach a user at all.
        RequestAuthorizationRegistry.AssertAllRequestsAreMarked(Assembly.GetExecutingAssembly());

        // Same idea one layer over: the administrator grant is two explicit lists rather than a
        // reflection loop, so a new permission constant cannot be granted by accident.
        AdministratorPermissionRegistry.AssertNoDivergence();

        return services;
    }
}
