using System.Collections.Concurrent;

namespace CleanArchitecture.Blazor.Application.Common.ExceptionHandlers;

internal static class ResultFailureFactory
{
    private static readonly ConcurrentDictionary<Type, Func<string[], object>> FailureFactoryCache = new();

    public static TResponse Create<TResponse>(params string[] errors)
        where TResponse : IResult
    {
        var factory = FailureFactoryCache.GetOrAdd(typeof(TResponse), CreateFailureFactory);
        return (TResponse)factory(errors);
    }

    /// <summary>
    /// The failure result for a <see cref="PostingRejectedException"/>: its message, and its code when it has
    /// one (Pass 60). Used by both <c>TransactionBehaviour</c> and <c>ResultExceptionBehavior</c>, so the two
    /// paths cannot drift.
    /// </summary>
    public static TResponse ForRejection<TResponse>(PostingRejectedException rejection)
        where TResponse : IResult =>
        string.IsNullOrWhiteSpace(rejection.ErrorCode)
            ? Create<TResponse>(rejection.Message)
            : Create<TResponse>(new ErrorCode(rejection.ErrorCode), rejection.Message);

    /// <summary>A failure carrying <paramref name="code"/> (Pass 60).</summary>
    public static TResponse Create<TResponse>(ErrorCode code, params string[] errors)
        where TResponse : IResult
    {
        var factory = CodedFailureFactoryCache.GetOrAdd(typeof(TResponse), CreateCodedFailureFactory);
        return (TResponse)factory(code, errors);
    }

    private static readonly ConcurrentDictionary<Type, Func<ErrorCode, string[], object>> CodedFailureFactoryCache = new();

    private static Func<ErrorCode, string[], object> CreateCodedFailureFactory(Type responseType)
    {
        var resultType = responseType.IsGenericType && responseType.GetGenericTypeDefinition() == typeof(Result<>)
            ? responseType
            : typeof(Result);
        var method = resultType.GetMethod("Failure", BindingFlags.Public | BindingFlags.Static, binder: null,
                         types: [typeof(ErrorCode), typeof(string[])], modifiers: null)
                     ?? throw new InvalidOperationException($"Could not find Failure(ErrorCode, ...) on {resultType.Name}.");

        var code = Expression.Parameter(typeof(ErrorCode), "code");
        var errors = Expression.Parameter(typeof(string[]), "errors");
        var call = Expression.Convert(Expression.Call(null, method, code, errors), typeof(object));
        return Expression.Lambda<Func<ErrorCode, string[], object>>(call, code, errors).Compile();
    }

    private static Func<string[], object> CreateFailureFactory(Type responseType)
    {
        var errorsParam = Expression.Parameter(typeof(string[]), "errors");

        if (responseType.IsGenericType && responseType.GetGenericTypeDefinition() == typeof(Result<>))
        {
            var valueType = responseType.GetGenericArguments()[0];
            var genericResultType = typeof(Result<>).MakeGenericType(valueType);
            var failureMethod = genericResultType.GetMethod(
                "Failure",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: [typeof(string[])],
                modifiers: null)
                ?? throw new InvalidOperationException($"Could not find the 'Failure' method on Result<{valueType.Name}>.");

            var call = Expression.Call(null, failureMethod, errorsParam);
            var cast = Expression.Convert(call, typeof(object));
            return Expression.Lambda<Func<string[], object>>(cast, errorsParam).Compile();
        }

        var resultFailureMethod = typeof(Result).GetMethod(
            "Failure",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [typeof(string[])],
            modifiers: null)
            ?? throw new InvalidOperationException("Could not find the 'Failure' method on Result.");

        var resultCall = Expression.Call(null, resultFailureMethod, errorsParam);
        var resultCast = Expression.Convert(resultCall, typeof(object));
        return Expression.Lambda<Func<string[], object>>(resultCast, errorsParam).Compile();
    }
}
