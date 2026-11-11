#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Caching;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Identity;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Transactions;
using CleanArchitecture.Blazor.Application.Common.Models;
using CleanArchitecture.Blazor.Application.Features.PicklistSets.Commands.Import;
using CleanArchitecture.Blazor.Application.Pipeline;
using FluentAssertions;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Pipeline;

/// <summary>
/// Pass 58, plan §5 rows 12 and 20: the pipeline order around TransactionBehaviour, the cache bypass
/// inside a unit of work, and ResultExceptionBehavior's error mapping.
/// </summary>
[TestFixture]
public class TransactionPipelineTests
{
    // ---- 20: order -----------------------------------------------------------------------------

    [Test]
    public void T20_CacheInvalidationRunsOutsideTheTransaction_AndTheTransactionInsideResultException()
    {
        var services = new ServiceCollection();
        CleanArchitecture.Blazor.Application.DependencyInjection.AddApplication(services);

        // ImportPicklistSetsCommand is both a cache invalidator and transactional: the probe.
        var order = services
            .Where(d => d.ServiceType.IsGenericType &&
                        d.ServiceType.GetGenericTypeDefinition() == typeof(IPipelineBehavior<,>) &&
                        d.ServiceType.GetGenericArguments()[0] == typeof(ImportPicklistSetsCommand))
            .Select(d => d.ImplementationType!.GetGenericTypeDefinition())
            .ToList();

        order.Should().Equal(
            typeof(AuthorizationBehaviour<,>),
            typeof(ValidationBehavior<,>),
            typeof(ResultExceptionBehavior<,>),
            typeof(CacheInvalidationBehaviour<,>),
            typeof(TransactionBehaviour<,>),
            typeof(PerformanceBehaviour<,>));
    }

    [Test]
    public void T20_FusionCacheIsInsideTheTransaction()
    {
        // Read from the source of truth for the array, since no template request is both cacheable
        // and transactional: FusionCache must come after Transaction in PipelineBehaviors.
        var source = System.IO.File.ReadAllText(FindRepoFile("src/Application/DependencyInjection.cs"));
        var transaction = source.IndexOf("typeof(TransactionBehaviour<,>)", StringComparison.Ordinal);
        var fusion = source.IndexOf("typeof(FusionCacheBehaviour<,>)", StringComparison.Ordinal);

        transaction.Should().BePositive();
        fusion.Should().BeGreaterThan(transaction);
    }

    [Test]
    public async Task T20_ACacheableQueryInsideAUnitOfWork_BypassesTheCache()
    {
        var cache = new Mock<IAppCache>(MockBehavior.Strict); // any call fails the test
        var behaviour = new FusionCacheBehaviour<CachedProbe, string>(
            cache.Object, new StubAccessor(), NullLogger<FusionCacheBehaviour<CachedProbe, string>>.Instance,
            new StubUnitOfWork(active: true));

        var result = await behaviour.Handle(new CachedProbe(), (_, _) => ValueTask.FromResult("fresh"), CancellationToken.None);

        result.Should().Be("fresh");
    }

    // ---- 12: error mapping ---------------------------------------------------------------------

    [Test]
    public async Task T12_AConcurrencyConflict_GetsItsOwnMessage_AndIsLoggedAtError()
    {
        var logger = new RecordingLogger<ResultExceptionBehavior<FailingRequest, Result>>();
        var behaviour = new ResultExceptionBehavior<FailingRequest, Result>(logger);

        var result = await behaviour.Handle(new FailingRequest(),
            (_, _) => throw new DbUpdateConcurrencyException("row version mismatch"), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Errors.Should().Equal(ResultExceptionBehavior<FailingRequest, Result>.ConcurrencyConflictMessage);
        logger.Levels.Should().Contain(LogLevel.Error);
    }

    [Test]
    public async Task T12_AnUnexpectedException_ShowsAReference_NotItsOwnText()
    {
        var logger = new RecordingLogger<ResultExceptionBehavior<FailingRequest, Result>>();
        var behaviour = new ResultExceptionBehavior<FailingRequest, Result>(logger);

        var result = await behaviour.Handle(new FailingRequest(),
            (_, _) => throw new InvalidOperationException("SELECT secret FROM table"), CancellationToken.None);

        result.Errors.Single().Should().StartWith("An unexpected error occurred. Reference: ")
            .And.NotContain("secret");
        logger.Levels.Should().Contain(LogLevel.Error);
    }

    [Test]
    public async Task T12_ADatabaseUpdateFailure_IsNowLoggedAtError()
    {
        var logger = new RecordingLogger<ResultExceptionBehavior<FailingRequest, Result>>();
        var behaviour = new ResultExceptionBehavior<FailingRequest, Result>(logger);

        var result = await behaviour.Handle(new FailingRequest(),
            (_, _) => throw new DbUpdateException("constraint"), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        logger.Levels.Should().Contain(LogLevel.Error);
    }

    // ---- harness -------------------------------------------------------------------------------

    public sealed class FailingRequest : IRequest<Result>;

    public sealed class CachedProbe : ICacheableRequest<string>
    {
        public string CacheKey => "probe";
        public CacheScope Scope => CacheScope.Global;
        public IEnumerable<string>? Tags => ["probe"];
    }

    private sealed class StubAccessor : IUserContextAccessor
    {
        public UserContext? Current => null;
        public IDisposable Push(UserContext? context) => throw new NotSupportedException();
        public void Clear() { }
        public void Set(UserContext context) => throw new NotSupportedException();
    }

    private sealed class StubUnitOfWork(bool active) : IUnitOfWork
    {
        public bool IsActive => active;
        public IApplicationDbContext Context => throw new NotSupportedException();
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogLevel> Levels { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Levels.Add(logLevel);
    }

    private static string FindRepoFile(string relative)
    {
        for (var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, relative);
            if (System.IO.File.Exists(candidate)) return candidate;
        }
        throw new System.IO.FileNotFoundException(relative);
    }
}
