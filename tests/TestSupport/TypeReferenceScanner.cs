using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace CleanArchitecture.Blazor.TestSupport;

/// <summary>One reference from a type to another, found in its metadata or in a method body.</summary>
/// <param name="From">The OUTERMOST declaring type: a compiler-generated nested type (async state machine,
/// iterator, closure, local function's display class) is attributed to the type whose code it holds.</param>
/// <param name="To">The referenced type.</param>
/// <param name="Where">Where it was found, for the failure message.</param>
public sealed record TypeReference(Type From, Type To, string Where)
{
    public override string ToString() => $"{From.FullName} -> {To.FullName} ({Where})";
}

/// <summary>
/// Every type a type references, read from its metadata and from the IL of every method body (Pass 60,
/// architecture rule 1). No package: <see cref="MethodBody.GetILAsByteArray"/>, the <see cref="OpCodes"/>
/// table, and <see cref="Module"/>'s token resolution.
/// </summary>
/// <remarks>
/// <para><b>What is read</b> (amendment C2: each of these is how a module boundary is actually crossed):</para>
/// <list type="bullet">
/// <item><description>the base type and interfaces, with their generic arguments;</description></item>
/// <item><description>custom attributes: the attribute type, and <see cref="Type"/> values in constructor and named
/// arguments (<c>[Sample(typeof(Other))]</c>);</description></item>
/// <item><description>field, property and event types; method return, parameter and local types; generic parameter
/// constraints;</description></item>
/// <item><description>every token in every method body: <c>call</c>/<c>callvirt</c>/<c>newobj</c> targets with their
/// declaring types and generic method arguments (<c>db.Set&lt;Other&gt;()</c>), field accesses, <c>castclass</c>,
/// <c>isinst</c>, <c>box</c>, <c>initobj</c>, and <c>ldtoken</c>, which is how an expression tree names a type or a
/// property (<c>x =&gt; x.Other.Name</c>);</description></item>
/// <item><description>all of the above for every nested type, recursively - closures, <c>async</c> state machines,
/// iterators - attributed to the outermost declaring type;</description></item>
/// <item><description>every referenced type is expanded through arrays, by-refs, pointers and generic arguments
/// (<c>List&lt;Other&gt;</c>, <c>Task&lt;Other[]&gt;</c>).</description></item>
/// </list>
/// <para>
/// A Razor component is an ordinary class after compilation (<c>BuildRenderTree</c>); its parameters are
/// properties and the components it renders are <c>OpenComponent&lt;T&gt;</c> generic method arguments.
/// </para>
/// <para>
/// A token that cannot be resolved is reported in <see cref="ScanResult.Unresolved"/>, never skipped silently: the
/// rule's tests assert there are none, so a gap shows as a failure rather than as a pass.
/// </para>
/// </remarks>
public static class TypeReferenceScanner
{
    private const BindingFlags All =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly OpCode[] OneByte = new OpCode[0x100];
    private static readonly OpCode[] TwoByte = new OpCode[0x100];

    static TypeReferenceScanner()
    {
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode code) continue;
            var value = (ushort)code.Value;
            if (value < 0x100) OneByte[value] = code;
            else if ((value & 0xff00) == 0xfe00) TwoByte[value & 0xff] = code;
        }
    }

    public sealed record ScanResult(IReadOnlyList<TypeReference> References, IReadOnlyList<string> Unresolved);

    /// <summary>Scans every type of <paramref name="assembly"/> (nested types through their outermost type).</summary>
    public static ScanResult Scan(Assembly assembly)
    {
        var references = new List<TypeReference>();
        var unresolved = new List<string>();
        foreach (var type in LoadableTypes(assembly).Where(t => !t.IsNested))
            ScanType(type, type, references, unresolved);
        return new ScanResult(references, unresolved);
    }

    /// <summary>Scans one top-level type and its nested types.</summary>
    public static ScanResult Scan(Type type)
    {
        var references = new List<TypeReference>();
        var unresolved = new List<string>();
        ScanType(Outermost(type), Outermost(type), references, unresolved);
        return new ScanResult(references, unresolved);
    }

    public static Type Outermost(Type type)
    {
        while (type.DeclaringType is { } outer) type = outer;
        return type;
    }

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.OfType<Type>(); }
    }

    private static void ScanType(Type owner, Type type, List<TypeReference> found, List<string> unresolved)
    {
        void Add(Type? target, string where)
        {
            foreach (var t in Expand(target)) found.Add(new TypeReference(owner, t, $"{type.Name}: {where}"));
        }

        Add(type.BaseType, "base type");
        foreach (var i in type.GetInterfaces()) Add(i, "interface");
        if (type.IsGenericTypeDefinition)
            foreach (var p in type.GetGenericArguments())
                foreach (var c in p.GetGenericParameterConstraints()) Add(c, $"constraint on {p.Name}");
        AddAttributes(type.GetCustomAttributesData(), "attribute", Add);

        foreach (var field in type.GetFields(All))
        {
            Add(field.FieldType, $"field {field.Name}");
            AddAttributes(field.GetCustomAttributesData(), $"attribute on {field.Name}", Add);
        }

        foreach (var property in type.GetProperties(All))
        {
            Add(property.PropertyType, $"property {property.Name}");
            AddAttributes(property.GetCustomAttributesData(), $"attribute on {property.Name}", Add);
        }

        foreach (var evt in type.GetEvents(All)) Add(evt.EventHandlerType, $"event {evt.Name}");

        foreach (var method in type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All)))
        {
            var name = method.Name;
            if (method is MethodInfo info) Add(info.ReturnType, $"{name} return");
            foreach (var parameter in method.GetParameters())
            {
                Add(parameter.ParameterType, $"{name} parameter {parameter.Name}");
                AddAttributes(parameter.GetCustomAttributesData(), $"attribute on {name}({parameter.Name})", Add);
            }
            AddAttributes(method.GetCustomAttributesData(), $"attribute on {name}", Add);
            if (method.IsGenericMethodDefinition)
                foreach (var p in method.GetGenericArguments())
                    foreach (var c in p.GetGenericParameterConstraints()) Add(c, $"{name} constraint on {p.Name}");

            MethodBody? body;
            try { body = method.GetMethodBody(); }
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException) { body = null; }
            if (body is null) continue;

            foreach (var local in body.LocalVariables) Add(local.LocalType, $"{name} local");
            ScanIl(method, body.GetILAsByteArray(), name, Add, unresolved);
        }

        foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            ScanType(owner, nested, found, unresolved);
    }

    private static void AddAttributes(IList<CustomAttributeData> attributes, string where, Action<Type?, string> add)
    {
        foreach (var attribute in attributes)
        {
            add(attribute.AttributeType, where);
            foreach (var argument in attribute.ConstructorArguments.Concat(attribute.NamedArguments.Select(n => n.TypedValue)))
                AddTypedArgument(argument, where, add);
        }
    }

    private static void AddTypedArgument(CustomAttributeTypedArgument argument, string where, Action<Type?, string> add)
    {
        switch (argument.Value)
        {
            case Type value:
                add(value, $"{where} typeof argument");
                break;
            case IEnumerable<CustomAttributeTypedArgument> items:
                foreach (var item in items) AddTypedArgument(item, where, add);
                break;
        }
    }

    private static void ScanIl(MethodBase method, byte[]? il, string name, Action<Type?, string> add, List<string> unresolved)
    {
        if (il is null) return;
        var module = method.Module;
        var typeArguments = method.DeclaringType is { IsGenericType: true } d ? d.GetGenericArguments() : null;
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;

        var position = 0;
        while (position < il.Length)
        {
            OpCode code;
            var first = il[position++];
            if (first == 0xfe)
            {
                if (position >= il.Length) break;
                code = TwoByte[il[position++]];
            }
            else
            {
                code = OneByte[first];
            }

            switch (code.OperandType)
            {
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    position += 1;
                    break;
                case OperandType.InlineVar:
                    position += 2;
                    break;
                case OperandType.InlineI:
                case OperandType.InlineBrTarget:
                case OperandType.ShortInlineR:
                case OperandType.InlineString:
                case OperandType.InlineSig:
                    position += 4;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    position += 8;
                    break;
                case OperandType.InlineSwitch:
                    var count = BitConverter.ToInt32(il, position);
                    position += 4 + 4 * count;
                    break;
                case OperandType.InlineType:
                case OperandType.InlineField:
                case OperandType.InlineMethod:
                case OperandType.InlineTok:
                    var token = BitConverter.ToInt32(il, position);
                    position += 4;
                    Resolve(module, token, code, typeArguments, methodArguments, $"{name} {code.Name}", add, unresolved);
                    break;
                default:
                    unresolved.Add($"{method.DeclaringType?.FullName}.{name}: unknown operand type {code.OperandType} at IL offset {position}");
                    return;
            }
        }
    }

    private static void Resolve(Module module, int token, OpCode code, Type[]? typeArguments, Type[]? methodArguments,
        string where, Action<Type?, string> add, List<string> unresolved)
    {
        try
        {
            var member = module.ResolveMember(token, typeArguments, methodArguments);
            switch (member)
            {
                case Type type:
                    add(type, where);
                    break;
                case FieldInfo field:
                    add(field.DeclaringType, $"{where} {field.Name}");
                    add(field.FieldType, $"{where} {field.Name}");
                    break;
                case MethodBase method:
                    add(method.DeclaringType, $"{where} {method.Name}");
                    if (method.IsGenericMethod)
                        foreach (var argument in method.GetGenericArguments()) add(argument, $"{where} {method.Name}<{argument.Name}>");
                    break;
            }
        }
        catch (Exception e) when (e is ArgumentException or TypeLoadException or FileNotFoundException or FileLoadException
                                      or BadImageFormatException or MissingMemberException)
        {
            unresolved.Add($"{where}: token 0x{token:x8} ({e.GetType().Name}: {e.Message})");
        }
    }

    /// <summary>A type and everything inside it: element types and generic arguments, recursively.</summary>
    public static IEnumerable<Type> Expand(Type? type)
    {
        if (type is null) yield break;
        if (type.HasElementType)
        {
            foreach (var t in Expand(type.GetElementType())) yield return t;
            yield break;
        }
        if (type.IsGenericParameter) yield break;

        yield return type.IsGenericType && !type.IsGenericTypeDefinition ? type.GetGenericTypeDefinition() : type;
        if (type.IsGenericType)
            foreach (var argument in type.GetGenericArguments())
                foreach (var t in Expand(argument)) yield return t;
    }

    /// <summary>True for a type the compiler generated (closures, state machines, anonymous types).</summary>
    public static bool IsCompilerGenerated(Type type) =>
        type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false) || type.Name.Contains('<');
}
