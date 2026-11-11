#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CleanArchitecture.Blazor.Application.Common.Concurrency;
using CleanArchitecture.Blazor.Application.Common.ExceptionHandlers;
using CleanArchitecture.Blazor.Application.Common.Models;
using CleanArchitecture.Blazor.Application.Pipeline;
using CleanArchitecture.Blazor.TestSupport;
using FluentAssertions;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Pipeline;

/// <summary>
/// Pass 60, Part D items 1 to 3 (plan §5 rows 28 to 30): no exception text reaches a user, a business
/// rejection keeps its message wherever it is thrown, and a failure can carry a machine-readable code.
/// </summary>
[TestFixture]
public class ErrorHandlingTests
{
    // ---- 28: D1 -------------------------------------------------------------------------------------

    [Test]
    public async Task T28_AnUnexpectedException_ReachesTheCallerAsAReference_WithTheDetailLogged()
    {
        var logger = new RecordingLogger<ResultExceptionBehavior<Probe, Result<int>>>();
        var behaviour = new ResultExceptionBehavior<Probe, Result<int>>(logger);

        var result = await behaviour.Handle(new Probe(),
            (_, _) => throw new InvalidOperationException("connection string Host=db;Password=secret"), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Errors.Single().Should().StartWith("An unexpected error occurred. Reference: ").And.NotContain("secret");
        result.ErrorCode.Should().Be(ErrorCode.Unexpected.Value);
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException);
    }

    [Test]
    public void T28_NoFailureResultInSrc_IsBuiltFromAnExceptionsMessage()
    {
        var findings = SourceTree.SourceFiles()
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .SelectMany(f => SourceRules.FailureResultsFromExceptionText(SourceTree.Relative(f), System.IO.File.ReadAllText(f)))
            .ToList();

        findings.Should().BeEmpty(
            "an exception's text can carry SQL, paths or data; log it and return a fixed message with a reference " +
            "(FaultReference.Log, or let ResultExceptionBehavior do it). Found:\n" + string.Join("\n", findings));
    }

    [Test]
    public void T28_TheScan_FailsOnAPlantedFailureBuiltFromAnExceptionsMessage()
    {
        const string planted = """
            try { Work(); }
            catch (IOException ex)
            {
                return Result.Failure($"Could not save: {ex.Message}");
            }
            """;
        SourceRules.FailureResultsFromExceptionText("Planted.cs", planted).Should().ContainSingle()
            .Which.Line.Should().Be(4);
    }

    [Test]
    public void T28_TheScan_AllowsTheExceptionsWrittenForUsers()
    {
        const string allowed = """
            catch (NotFoundException exception) { return ResultFailureFactory.Create<TResponse>(exception.Message); }
            catch (PostingRejectedException rejection) { return Result.Failure(rejection.Message); }
            """;
        SourceRules.FailureResultsFromExceptionText("Allowed.cs", allowed).Should().BeEmpty();
    }

    // ---- 29: D2 -------------------------------------------------------------------------------------

    [Test]
    public async Task T29_ARejectionOutsideAUnitOfWork_ReturnsItsOwnMessage_LoggedAtInformation()
    {
        var logger = new RecordingLogger<ResultExceptionBehavior<Probe, Result<int>>>();
        var behaviour = new ResultExceptionBehavior<Probe, Result<int>>(logger);

        var result = await behaviour.Handle(new Probe(),
            (_, _) => throw new PostingRejectedException("Insufficient stock for item X", "INSUFFICIENT_STOCK"), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Errors.Should().Equal("Insufficient stock for item X");
        logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Information);
        logger.Entries.Single().Message.Should().Contain("INSUFFICIENT_STOCK");
    }

    // ---- 30: D3 -------------------------------------------------------------------------------------

    [Test]
    public async Task T30_ARejectionsCode_ReachesTheCaller()
    {
        var behaviour = new ResultExceptionBehavior<Probe, Result<int>>(new RecordingLogger<ResultExceptionBehavior<Probe, Result<int>>>());

        var coded = await behaviour.Handle(new Probe(),
            (_, _) => throw new PostingRejectedException("No stock", "INSUFFICIENT_STOCK"), CancellationToken.None);
        var uncoded = await behaviour.Handle(new Probe(),
            (_, _) => throw new PostingRejectedException("No stock"), CancellationToken.None);

        coded.ErrorCode.Should().Be("INSUFFICIENT_STOCK");
        uncoded.ErrorCode.Should().BeNull();
    }

    [Test]
    public async Task T30_AConflict_CarriesItsCode_AndIsStillRecognised()
    {
        var behaviour = new ResultExceptionBehavior<PlainProbe, Result>(new RecordingLogger<ResultExceptionBehavior<PlainProbe, Result>>());
        var result = await behaviour.Handle(new PlainProbe(), (_, _) => throw new DbUpdateConcurrencyException("x"), CancellationToken.None);

        result.ErrorCode.Should().Be("GX.CONCURRENCY_CONFLICT");
        ConcurrencyConflict.Is(result).Should().BeTrue();

        // A result built before Pass 60, with the message only, is still recognised.
        ConcurrencyConflict.Is(Result.Failure(ConcurrencyConflict.Message)).Should().BeTrue();
        ConcurrencyConflict.Is(Result.Failure("Something else")).Should().BeFalse();
    }

    [Test]
    public void T30_ExistingFailureCalls_AreUnchanged()
    {
        var two = Result.Failure("a", "b");
        two.Errors.Should().Equal("a", "b");
        two.ErrorCode.Should().BeNull();

        var typed = Result<int>.Failure("a", "b");
        typed.Errors.Should().Equal("a", "b");
        typed.ErrorCode.Should().BeNull();
    }

    [Test]
    public void T30_TheCode_IsCarriedThroughMapAndBind()
    {
        var failed = Result<int>.Failure(new ErrorCode("X.CODE"), "failed");

        failed.Map(i => i.ToString()).ErrorCode.Should().Be("X.CODE");
        failed.Bind(i => Result<string>.Success(i.ToString())).ErrorCode.Should().Be("X.CODE");
        failed.Map(i => i.ToString()).Errors.Should().Equal("failed");
        ((CleanArchitecture.Blazor.Application.Common.Interfaces.IResult)failed).ErrorCode.Should().Be("X.CODE", "the interface member reads the same code");
    }

    [Test]
    public void T30_AnEmptyCode_IsRefused()
    {
        var act = () => new ErrorCode(" ");
        act.Should().Throw<ArgumentException>();
    }

    // ---- harness ------------------------------------------------------------------------------------

    public sealed class Probe : IRequest<Result<int>>;

    public sealed class PlainProbe : IRequest<Result>;

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
