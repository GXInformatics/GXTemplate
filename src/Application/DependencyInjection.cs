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
            //
            // Pass 58: CacheInvalidation moved OUTSIDE Transaction, so a cache is cleared only after
            // the transaction has committed (or the handler has returned), never before; and
            // Transaction sits inside ResultException, so an exception it rethrows after rolling back
            // is still turned into a failure result.
            options.PipelineBehaviors = [
                typeof(AuthorizationBehaviour<,>),
                // Pass 59: applies only to IBranchCommand requests; refuses a branch the user may not
                // write to, and grants the branch to the rest of the call chain when it may.
                typeof(BranchAccessBehaviour<,>),
                typeof(ValidationBehavior<,>),
                typeof(ResultExceptionBehavior<,>),
                typeof(CacheInvalidationBehaviour<,>),
                typeof(TransactionBehaviour<,>),
                typeof(PerformanceBehaviour<,>),
                typeof(FusionCacheBehaviour<,>)
                ];

        });

        // Posting transactions (Pass 58): in-transaction event handlers, dispatched by
        // TransactionBehaviour rather than by Mediator's background publisher.
        services.AddScoped<IPostingEventDispatcher, PostingEventDispatcher>();
        services.AddScoped<CleanArchitecture.Blazor.Application.Common.Branches.IBranchQueryScope,
            CleanArchitecture.Blazor.Application.Common.Branches.BranchQueryScope>();
        services.AddScoped<CleanArchitecture.Blazor.Application.Features.Branches.IUserBranchDirectory,
            CleanArchitecture.Blazor.Application.Features.Branches.UserBranchDirectory>();
        services.AddPostingEventHandlers(Assembly.GetExecutingAssembly());
       
        // Deny-by-default is enforced at dispatch time by AuthorizationBehaviour; this fails the
        // application at startup instead, so an unmarked request cannot reach a user at all.
        RequestAuthorizationRegistry.AssertAllRequestsAreMarked(Assembly.GetExecutingAssembly());

        // Same idea one layer over: the administrator grant is two explicit lists rather than a
        // reflection loop, so a new permission constant cannot be granted by accident.
        AdministratorPermissionRegistry.AssertNoDivergence();

        return services;
    }
}
