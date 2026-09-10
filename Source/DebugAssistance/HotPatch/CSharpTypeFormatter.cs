namespace DebugAssistance.HotPatch;

// Shared by ProjectScaffolder, HotPatchManager, and HotPatchEndpoints wherever a MethodBase or
// Type needs to be shown as valid, readable C# rather than Type.FullName/ToString()'s CLR
// notation (backticks and bracketed, assembly-qualified generic argument lists).
internal static class CSharpTypeFormatter
{
    private static readonly Dictionary<Type, string> KnownTypeNames = new()
    {
        [typeof(void)] = "void",
        [typeof(bool)] = "bool",
        [typeof(byte)] = "byte",
        [typeof(sbyte)] = "sbyte",
        [typeof(short)] = "short",
        [typeof(ushort)] = "ushort",
        [typeof(int)] = "int",
        [typeof(uint)] = "uint",
        [typeof(long)] = "long",
        [typeof(ulong)] = "ulong",
        [typeof(float)] = "float",
        [typeof(double)] = "double",
        [typeof(decimal)] = "decimal",
        [typeof(char)] = "char",
        [typeof(string)] = "string",
        [typeof(object)] = "object",
    };

    internal static string DescribeMethod(MethodBase method) =>
        method.DeclaringType is { } type ? $"{FormatType(type)}.{method.Name}" : method.Name;

    // Some runtime type graphs (observed for a generic collection's nested Enumerator, reached
    // through a dynamically-generated Harmony patch method) report DeclaringType/generic-argument
    // metadata that recurses indefinitely instead of terminating. MaxDepth is a hard backstop on
    // top of the `seen`-based cycle check below, so a metadata shape neither of us has seen yet
    // still can't overflow the stack.
    private const int MaxDepth = 64;

    internal static string FormatType(Type type) => FormatType(type, [], 0);

    // `seen` tracks the chain of types currently being formatted (not sibling generic arguments)
    // so a genuinely self-referential type graph -- which reflection can produce for some
    // generic instantiations -- breaks the recursion instead of overflowing the stack.
    private static string FormatType(Type type, HashSet<Type> seen, int depth)
    {
        if (KnownTypeNames.TryGetValue(type, out var knownName))
        {
            return knownName;
        }

        if (depth >= MaxDepth || !seen.Add(type))
        {
            return type.Name;
        }

        try
        {
            if (type.IsByRef)
            {
                return FormatType(type.GetElementType(), seen, depth + 1);
            }

            if (type.IsArray)
            {
                var rank = type.GetArrayRank();
                return $"{FormatType(type.GetElementType(), seen, depth + 1)}[{new string(',', rank - 1)}]";
            }

            if (Nullable.GetUnderlyingType(type) is { } underlyingType)
            {
                return $"{FormatType(underlyingType, seen, depth + 1)}?";
            }

            if (type.IsGenericType)
            {
                var name = type.Name;
                var tickIndex = name.IndexOf('`', StringComparison.Ordinal);
                if (tickIndex >= 0)
                {
                    name = name[..tickIndex];
                }

                var prefix =
                    type.IsNested && type.DeclaringType is { } declaringType
                        ? $"{FormatType(declaringType, seen, depth + 1)}."
                    : type.Namespace is { Length: > 0 } ns ? $"{ns}."
                    : "";
                var genericArgs = string.Join(
                    ", ",
                    type.GetGenericArguments().Select(t => FormatType(t, seen, depth + 1))
                );
                return $"{prefix}{name}<{genericArgs}>";
            }

            return type.IsNested && type.DeclaringType is { } parentType
                ? $"{FormatType(parentType, seen, depth + 1)}.{type.Name}"
                : type.FullName?.Replace('+', '.') ?? type.Name;
        }
        finally
        {
            _ = seen.Remove(type);
        }
    }
}
