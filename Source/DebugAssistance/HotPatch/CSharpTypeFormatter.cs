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

    internal static string FormatType(Type type)
    {
        if (KnownTypeNames.TryGetValue(type, out var knownName))
        {
            return knownName;
        }

        if (type.IsByRef)
        {
            return FormatType(type.GetElementType());
        }

        if (type.IsArray)
        {
            var rank = type.GetArrayRank();
            return $"{FormatType(type.GetElementType())}[{new string(',', rank - 1)}]";
        }

        if (Nullable.GetUnderlyingType(type) is { } underlyingType)
        {
            return $"{FormatType(underlyingType)}?";
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
                    ? $"{FormatType(declaringType)}."
                : type.Namespace is { Length: > 0 } ns ? $"{ns}."
                : "";
            var genericArgs = string.Join(", ", type.GetGenericArguments().Select(FormatType));
            return $"{prefix}{name}<{genericArgs}>";
        }

        return type.IsNested && type.DeclaringType is { } parentType
            ? $"{FormatType(parentType)}.{type.Name}"
            : type.FullName?.Replace('+', '.') ?? type.Name;
    }
}
