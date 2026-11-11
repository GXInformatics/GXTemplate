using System.Collections.Concurrent;

namespace CleanArchitecture.Blazor.Application.Common.Transactions;

/// <summary>
/// Stores and rebuilds the response of an idempotent request (Pass 58).
/// </summary>
/// <remarks>
/// Only a SUCCESSFUL response is ever recorded - a failure rolls the claim back - so the stored form
/// is the <c>Data</c> alone, as JSON, and the response is rebuilt with <c>Result.Success()</c> or
/// <c>Result&lt;T&gt;.Success(data)</c>. That keeps <see cref="Result"/> free of serialisation
/// constructors. Any other response type is refused before the handler runs.
/// </remarks>
internal static class IdempotentResponseCodec
{
    private static readonly ConcurrentDictionary<Type, Type?> DataTypes = new();

    /// <summary>True for <c>Result</c> and <c>Result&lt;T&gt;</c>.</summary>
    public static bool Supports(Type responseType) => responseType == typeof(Result) || DataTypeOf(responseType) is not null;

    public static string Serialize<TResponse>(TResponse response) where TResponse : IResult
    {
        var dataType = DataTypeOf(typeof(TResponse));
        if (dataType is null) return "null";

        var data = typeof(TResponse).GetProperty(nameof(Result<object>.Data))!.GetValue(response);
        return JsonSerializer.Serialize(data, dataType, JsonSerializerOptions.Web);
    }

    public static TResponse Deserialize<TResponse>(string? stored) where TResponse : IResult
    {
        if (typeof(TResponse) == typeof(Result)) return (TResponse)(object)Result.Success();

        var dataType = DataTypeOf(typeof(TResponse))
                       ?? throw new NotSupportedException($"{typeof(TResponse).Name} cannot be rebuilt from an idempotency record.");
        var data = string.IsNullOrEmpty(stored) ? null : JsonSerializer.Deserialize(stored, dataType, JsonSerializerOptions.Web);
        var success = typeof(TResponse).GetMethod(nameof(Result<object>.Success), BindingFlags.Public | BindingFlags.Static,
            [dataType])!;
        return (TResponse)success.Invoke(null, [data])!;
    }

    private static Type? DataTypeOf(Type responseType) =>
        DataTypes.GetOrAdd(responseType, static t =>
            t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Result<>) ? t.GetGenericArguments()[0] : null);
}
