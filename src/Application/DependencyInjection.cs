// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using CleanArchitecture.Blazor.Application.Common.Mappings;
using CleanArchitecture.Blazor.Application.Common.Modules;
using CleanArchitecture.Blazor.Application.Common.PublishStrategies;
using CleanArchitecture.Blazor.Application.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Mediator;

namespace CleanArchitecture.Blazor.Application;

public static class DependencyInjection
{
    /// <param name="configuration">Passed to the modules' registrations (Pass 60b); an empty one when omitted.</param>
    public static IServiceCollection AddApplication(this IServiceCollection services,
        Microsoft.Extensions.Configuration.IConfiguration? configuration = null)
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
                // Pass 60b: applies only to IBranchDefiningCommand requests; grants the branch the command saves to the rest
                // of the command and its posting-event handlers. Outside the unit of work, so it spans every round.
                typeof(BranchDefinitionGrantBehaviour<,>),
                typeof(ValidationBehavior<,>),
                typeof(ResultExceptionBehavior<,>),
                typeof(CacheInvalidationBehaviour<,>),
                typeof(TransactionBehaviour<,>),
                // Pass 60: applies only to IApprovalCommand requests; inside the unit of work so the header is
                // locked while the approval policy reads it, and before the handler.
                typeof(ApprovalBehaviour<,>),
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

        // Pass 60b: the modules' and the application level's own Application services, after the template's.
        services.AddModuleServices(Assembly.GetExecutingAssembly(), configuration);

        // Deny-by-default is enforced at dispatch time by AuthorizationBehaviour; this fails the
        // application at startup instead, so an unmarked request cannot reach a user at all.
        RequestAuthorizationRegistry.AssertAllRequestsAreMarked(Assembly.GetExecutingAssembly());

        // Pass 60: the request contracts (an idempotent request returns Result; an approval command fits its
        // header), at startup instead of at the first send.
        RequestContractRegistry.AssertSound(Assembly.GetExecutingAssembly());

        // Same idea one layer over: the administrator grant is two explicit lists rather than a
        // reflection loop, so a new permission constant cannot be granted by accident.
        AdministratorPermissionRegistry.AssertNoDivergence();

        return services;
    }
}
