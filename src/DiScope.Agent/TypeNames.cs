namespace DiScope.Agent;

/// <summary>Formats CLR types the way C# developers write them: <c>Ns.IRepository&lt;Ns.Order&gt;</c>.</summary>
internal static class TypeNames
{
    private static readonly Dictionary<Type, string> Keywords = new()
    {
        [typeof(bool)] = "bool", [typeof(byte)] = "byte", [typeof(sbyte)] = "sbyte", [typeof(char)] = "char",
        [typeof(short)] = "short", [typeof(ushort)] = "ushort", [typeof(int)] = "int", [typeof(uint)] = "uint",
        [typeof(long)] = "long", [typeof(ulong)] = "ulong", [typeof(float)] = "float", [typeof(double)] = "double",
        [typeof(decimal)] = "decimal", [typeof(string)] = "string", [typeof(object)] = "object", [typeof(void)] = "void",
    };

    public static string Format(Type type)
    {
        if (type.IsGenericParameter) return type.Name;
        if (Keywords.TryGetValue(type, out var keyword)) return keyword;
        if (type.IsArray) return Format(type.GetElementType()!) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        if (type.IsByRef) return Format(type.GetElementType()!);
        if (!type.IsGenericTypeDefinition && Nullable.GetUnderlyingType(type) is { } underlying) return Format(underlying) + "?";

        var allArgs = type.IsGenericType ? type.GetGenericArguments() : Type.EmptyTypes;
        return FormatCore(type, allArgs);
    }

    // Nested types in generic types share the generic arguments of their declaring types,
    // so walk outwards and give each level only the arguments it declares.
    private static string FormatCore(Type type, Type[] allArgs)
    {
        string prefix;
        var inherited = 0;
        if (type.IsNested && type.DeclaringType is { } declaring)
        {
            inherited = declaring.IsGenericType ? declaring.GetGenericArguments().Length : 0;
            prefix = FormatCore(declaring, allArgs.Take(inherited).ToArray()) + ".";
        }
        else
        {
            prefix = string.IsNullOrEmpty(type.Namespace) ? "" : type.Namespace + ".";
        }

        var name = StripArity(type.Name);
        var own = allArgs.Skip(inherited).ToArray();
        return own.Length == 0 ? prefix + name : prefix + name + "<" + string.Join(", ", own.Select(Format)) + ">";
    }

    /// <summary>Full name without generic arity or arguments, for editor navigation: <c>Ns.Outer.Repository</c>.</summary>
    public static string SourceName(Type type)
    {
        while (type.HasElementType) type = type.GetElementType()!;
        if (type.IsGenericType) type = type.GetGenericTypeDefinition();
        var name = StripArity(type.Name);
        if (type.IsNested && type.DeclaringType is { } declaring) return SourceName(declaring) + "." + name;
        return string.IsNullOrEmpty(type.Namespace) ? name : type.Namespace + "." + name;
    }

    private static string StripArity(string name)
    {
        var tick = name.IndexOf('`');
        return tick < 0 ? name : name[..tick];
    }
}
