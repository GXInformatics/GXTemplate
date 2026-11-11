using System.Diagnostics;

namespace CleanArchitecture.Blazor.Infrastructure.Services;

/// <summary>
/// Logs a fault with a reference and returns the reference, so that a failure result can name the log entry
/// without carrying the exception's own text (Pass 60).
/// </summary>
/// <remarks>
/// An exception's message can carry paths, SQL, table names or data. <c>ResultExceptionBehavior</c> already
/// shows the user a fixed message and a reference; this is the same rule for the services that catch their
/// own exceptions and return a <c>Result</c>. The architecture scan (<c>ExceptionTextScanTests</c>) fails on
/// a failure result built from an exception's message.
/// </remarks>
internal static class FaultReference
{
    public static string Log(ILogger? logger, Exception exception, string operation)
    {
        var reference = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        logger?.LogError(exception, "{Operation} failed (reference {ErrorReference})", operation, reference);
        return reference;
    }
}
