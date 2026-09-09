using System.Collections.Concurrent;

namespace DebugAssistance.HotPatch;

// A method found while browsing a set of assemblies for the hot-patch pickers below.
internal readonly record struct BrowsedMethod(
    Assembly Assembly,
    Type DeclaringType,
    MethodBase Method
);

// One namespace found while browsing a single assembly's types for the tree-style method picker.
// Name is "" for types declared outside any namespace, which is itself a valid, browsable group.
internal readonly record struct BrowsedNamespace(string Name, int TypeCount);

// Enumerates types/methods across a set of assemblies for both hot-patch pickers: the
// target-method picker (every currently loaded assembly) and the patch-method picker (just the
// one player-loaded assembly, since the player must say which of its methods is the
// Prefix/Postfix/Transpiler/Finalizer). One implementation serves both.
internal static class MethodBrowser
{
    private const BindingFlags AllDeclaredMethods =
        BindingFlags.Public
        | BindingFlags.NonPublic
        | BindingFlags.Instance
        | BindingFlags.Static
        | BindingFlags.DeclaredOnly;

    // A loaded Assembly's own types/methods never change after the fact, and reloading a path
    // always produces a brand new Assembly instance for it rather than mutating the old one (see
    // LiveAssemblyLoader.Load/HotPatchEndpoints.ResolveAssemblyByFullName), so caching keyed on
    // the Assembly/Type instance itself needs no invalidation hook. Every browse level funnels
    // through GetLoadableTypes/GetLoadableMethods below, so caching there covers the whole tree
    // (namespaces/types/method-counts) without needing its own cache per level.
    private static readonly ConcurrentDictionary<Assembly, Type[]> TypesCache = new();
    private static readonly ConcurrentDictionary<Type, MethodInfo[]> MethodsCache = new();

    // A filter containing a "." splits into a type-name part (everything before the last ".") and
    // a method-name part (everything after), so "CompGlower.PostExposeData" narrows to methods
    // named like PostExposeData declared on a type named like CompGlower. Without a ".", the whole
    // filter is checked against both the method name and the declaring type's name, so e.g.
    // "CompGlower" alone lists every method that type declares.
    internal static List<BrowsedMethod> Browse(
        IEnumerable<Assembly> assemblies,
        string? nameFilter = null
    )
    {
        string? typeFilter = null;
        var methodFilter = nameFilter;
        if (nameFilter is not null)
        {
            var lastDot = nameFilter.LastIndexOf('.');
            if (lastDot >= 0)
            {
                typeFilter = nameFilter[..lastDot];
                methodFilter = nameFilter[(lastDot + 1)..];
            }
        }

        List<BrowsedMethod> results = [];
        foreach (var assembly in assemblies)
        {
            foreach (var type in GetLoadableTypes(assembly))
            {
                var typeName = type.FullName ?? type.Name;
                if (
                    typeFilter is not null
                    && !typeName.Contains(typeFilter, StringComparison.OrdinalIgnoreCase)
                )
                {
                    continue;
                }

                var typeNameAlsoMatches =
                    typeFilter is null
                    && methodFilter is not null
                    && typeName.Contains(methodFilter, StringComparison.OrdinalIgnoreCase);

                foreach (var method in GetLoadableMethods(type))
                {
                    if (
                        methodFilter is not null
                        && !typeNameAlsoMatches
                        && !method.Name.Contains(methodFilter, StringComparison.OrdinalIgnoreCase)
                    )
                    {
                        continue;
                    }

                    results.Add(new BrowsedMethod(assembly, type, method));
                }
            }
        }
        return results;
    }

    // First level of the tree-style picker (Assembly -> Namespace -> Type -> Member): every
    // distinct namespace declared by a type in `assembly`, with how many types fall under it.
    internal static List<BrowsedNamespace> BrowseNamespaces(Assembly assembly) =>
        [
            .. GetLoadableTypes(assembly)
                .GroupBy(type => type.Namespace ?? "")
                .Select(group => new BrowsedNamespace(group.Key, group.Count()))
                .OrderBy(ns => ns.Name, StringComparer.OrdinalIgnoreCase),
        ];

    // Second level: every type declared directly under `ns` within `assembly` (nested types
    // included, same as GetLoadableTypes always has — they group under their containing type's
    // own namespace, not a synthetic child namespace).
    internal static List<Type> BrowseTypes(Assembly assembly, string ns) =>
        [
            .. GetLoadableTypes(assembly)
                .Where(type => (type.Namespace ?? "") == ns)
                .OrderBy(type => type.Name, StringComparer.OrdinalIgnoreCase),
        ];

    // Leaf level: every method declared directly on `type`, for when the picker already knows
    // exactly which type it's listing members of.
    internal static List<BrowsedMethod> BrowseMethods(Assembly assembly, Type type) =>
        [.. GetLoadableMethods(type).Select(method => new BrowsedMethod(assembly, type, method))];

    // MethodBase.ToString() never names its parameters, so near-duplicate overloads look identical
    // in the picker, and a Harmony error naming one of the patch author's own parameters (e.g.
    // "Parameter \"c\" not found in ...") can't be mapped back to anything visible. This keeps
    // ToString()'s own look (its "Boolean ByRef"-style wording for ref parameters, and its
    // System.* namespace stripping) but adds each parameter's name.
    internal static string FormatSignature(MethodBase method)
    {
        var returnTypeName = method is MethodInfo methodInfo
            ? FormatTypeName(methodInfo.ReturnType)
            : "Void";
        var parameters = string.Join(", ", method.GetParameters().Select(FormatParameter));
        return $"{returnTypeName} {method.Name}({parameters})";
    }

    private static string FormatParameter(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        var isByRef = type.IsByRef;
        var typeName = FormatTypeName(isByRef ? type.GetElementType() : type);
        return isByRef ? $"{typeName} ByRef {parameter.Name}" : $"{typeName} {parameter.Name}";
    }

    private static string FormatTypeName(Type type) =>
        type.Namespace == nameof(System) ? type.Name : type.ToString();

    // A single type that fails to load (missing dependency, version mismatch, an interface it
    // claims to implement but doesn't) makes GetTypes() throw for the WHOLE assembly, on runtimes
    // that surface the failure this early — ReflectionTypeLoadException.Types still carries every
    // type that DID load, with a null in place of each failure, so recovering here keeps the rest
    // of the assembly browsable instead of losing it entirely.
    internal static Type[] GetLoadableTypes(Assembly assembly) =>
        TypesCache.GetOrAdd(
            assembly,
            static a =>
            {
                try
                {
                    return a.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    return [.. ex.Types.OfType<Type>()];
                }
            }
        );

    // On the Mono runtime RimWorld embeds, a type that merely claims to implement an interface
    // without providing the method loads fine at GetTypes() time — the failure only surfaces once
    // something actually resolves its vtable, which enumerating its members does. Guarding here
    // (in addition to GetLoadableTypes' own guard) is what keeps that kind of type from taking
    // down the whole browse instead of just being skipped.
    private static MethodInfo[] GetLoadableMethods(Type type) =>
        MethodsCache.GetOrAdd(
            type,
            static t =>
            {
                try
                {
                    return t.GetMethods(AllDeclaredMethods);
                }
                catch (Exception ex) when (ex is TypeLoadException or ReflectionTypeLoadException)
                {
                    return [];
                }
            }
        );
}
