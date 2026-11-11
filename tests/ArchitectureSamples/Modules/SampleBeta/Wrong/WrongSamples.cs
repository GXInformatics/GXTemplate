using CleanArchitecture.Blazor.Application.Common.Interfaces;
using CleanArchitecture.Blazor.ArchitectureSamples.Modules.SampleAlpha.Entities;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;

namespace CleanArchitecture.Blazor.ArchitectureSamples.Modules.SampleBeta.Wrong;

// Amendment C2. Each type below crosses into module SampleAlpha in exactly one way, and rule 1 must fail on
// each, separately. Nothing else in a type refers to SampleAlpha, so a pass on one of them is a gap.

/// <summary>a. A generic method argument: <c>db.Set&lt;SampleAlphaItem&gt;()</c>.</summary>
public sealed class CaseA_GenericMethodArgument
{
    public Task<int> CountAsync(IApplicationDbContext db) => db.Set<SampleAlphaItem>().CountAsync();
}

/// <summary>b. An expression-tree lambda reading another module's entity property.</summary>
public sealed class CaseB_ExpressionLambda
{
    public Expression<Func<object, string>> Name() => o => ((SampleAlphaItem)o).Name;
}

/// <summary>b. The same as a delegate lambda (compiled into a closure class).</summary>
public sealed class CaseB_DelegateLambda
{
    public Func<object, string> Name() => o => ((SampleAlphaItem)o).Name;
}

/// <summary>c. Inside an async method: the code is in the compiler's state machine.</summary>
public sealed class CaseC_AsyncMethod
{
    public async Task<string> NameAsync(object o)
    {
        await Task.Yield();
        return ((SampleAlphaItem)o).Name;
    }
}

/// <summary>d. Inside an iterator: the code is in the compiler's iterator class.</summary>
public sealed class CaseD_Iterator
{
    public IEnumerable<string> Names(IEnumerable<object> items)
    {
        foreach (var o in items) yield return ((SampleAlphaItem)o).Name;
    }
}

/// <summary>d. Inside a local function.</summary>
public sealed class CaseD_LocalFunction
{
    public string Name(object o)
    {
        return Read(o);

        static string Read(object x) => ((SampleAlphaItem)x).Name;
    }
}

/// <summary>f. A generic argument in a field type.</summary>
public sealed class CaseF_FieldGenericArgument
{
#pragma warning disable CS0414, CS0169
    private List<SampleAlphaItem>? _items;
#pragma warning restore CS0414, CS0169
}

/// <summary>A base class of Beta's own, generic.</summary>
public abstract class BetaRepositoryBase<T>;

/// <summary>f. A generic argument in a base type.</summary>
public sealed class CaseF_BaseTypeGenericArgument : BetaRepositoryBase<SampleAlphaItem>;

/// <summary>An attribute of Beta's own that takes a type.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class BetaTagAttribute(Type tagged) : Attribute
{
    public Type Tagged { get; } = tagged;
}

/// <summary>f. A type argument in an attribute.</summary>
[BetaTag(typeof(SampleAlphaItem))]
public sealed class CaseF_AttributeTypeArgument;

/// <summary>Pass 60b, amendment E2: a module references application-level code, which no module may.</summary>
public sealed class CaseG_ReferencesAppLevel
{
    public string Banner() => CleanArchitecture.Blazor.ArchitectureSamples.AppLevel.AppLevelHelper.Banner;
}

/// <summary>Pass 60b, amendment E2: a module sends another module's command that is not in its Contracts (rule 9, part of rule 1).</summary>
public sealed class CaseH_SendsAnotherModulesCommand
{
    public object Build() => new CleanArchitecture.Blazor.ArchitectureSamples.Modules.SampleAlpha.Features.CreateAlphaItemCommand("x");
}
