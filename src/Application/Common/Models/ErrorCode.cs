namespace CleanArchitecture.Blazor.Application.Common.Models;

/// <summary>
/// A machine-readable code carried by a failed <see cref="Result"/> (Pass 60), such as
/// <c>INSUFFICIENT_STOCK</c> or <see cref="ConcurrencyConflict"/>.
/// </summary>
/// <remarks>
/// A distinct type, not a string, so that <c>Result.Failure(new ErrorCode("X"), "message")</c> can never be
/// confused with <c>Result.Failure("first message", "second message")</c>, which still means two messages.
/// The code is for programs; the user reads <see cref="Result.Errors"/>.
/// </remarks>
public readonly record struct ErrorCode
{
    public ErrorCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("An error code cannot be empty.", nameof(value));
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;

    /// <summary>The record was changed by someone else since it was loaded (pass 59).</summary>
    public static readonly ErrorCode ConcurrencyConflict = new("GX.CONCURRENCY_CONFLICT");

    /// <summary>An unexpected fault; the user sees a fixed message with a reference (pass 58).</summary>
    public static readonly ErrorCode Unexpected = new("GX.UNEXPECTED");
}
