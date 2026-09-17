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

// Where in the Assembly -> Namespace -> Type tree a flat search is being issued from, so Browse
// can rank methods declared there above equally-matching methods declared elsewhere instead of
// treating every loaded type as equally relevant. Only the deepest set field is checked --
// TypeFullName implies AssemblyFullName is also set (a type can't be scoped without its assembly),
// same for Namespace.
internal readonly record struct BrowseScope(
    string? AssemblyFullName,
    string? Namespace,
    string? TypeFullName
);

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
    private static readonly ConcurrentDictionary<Type, MethodBase[]> MethodsCache = new();

    // A filter containing a "." splits into a type-name part (everything before the last ".") and
    // a method-name part (everything after), so "CompGlower.PostExposeData" narrows to methods
    // named like PostExposeData declared on a type named like CompGlower. Without a ".", the whole
    // filter is checked against both the method name and the declaring type's name, so e.g.
    // "CompGlower" alone lists every method that type declares.
    //
    // Results are ranked by how well the filter matches (exact name, then prefix, then a
    // camelCase/namespace word boundary, then a bare substring anywhere) before being returned, so
    // e.g. "pawn.kill" puts Pawn.Kill itself ahead of Pawn methods that merely contain "kill" as
    // a substring (like DoKillSideEffects) or types that merely contain "pawn" (like
    // StartingPawnUtility). Methods declared within `scope` (wherever the picker's tree navigation
    // currently sits) always rank above equally-matching methods outside it, so e.g. searching
    // "Draw" while browsing Pawn's methods puts Pawn.Draw ahead of an unrelated DrawStyle.Draw
    // instead of leaving them tied.
    internal static List<BrowsedMethod> Browse(
        IEnumerable<Assembly> assemblies,
        string? nameFilter = null,
        BrowseScope scope = default
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

        List<(int Score, bool InScope, BrowsedMethod Method)> scored = [];
        foreach (var assembly in assemblies)
        {
            foreach (var type in GetLoadableTypes(assembly))
            {
                var inScope = IsInScope(assembly, type, scope);
                var typeName = type.FullName ?? type.Name;
                var typeScore = 0;
                if (typeFilter is not null)
                {
                    typeScore = MatchScore(typeName, typeFilter);
                    if (typeScore < 0)
                    {
                        continue;
                    }
                }

                // Without a ".", the whole filter is also checked against the declaring type's
                // name, so a filter like "CompGlower" alone still lists every method that type
                // declares even though none of their names contain "CompGlower".
                var typeNameMatchScore =
                    typeFilter is null && methodFilter is not null
                        ? MatchScore(typeName, methodFilter)
                        : -1;

                foreach (var method in GetLoadableMethods(type))
                {
                    // A constructor's real Method.Name is ".ctor"/".cctor", which a filter like
                    // "Pawn.Pawn" (or a bare method-name filter with a typeFilter already set)
                    // could never match -- match against the same name FormatSignature displays
                    // for it (the declaring type's own name) instead.
                    var methodDisplayName = method is ConstructorInfo ? type.Name : method.Name;
                    var methodScore = methodFilter is null
                        ? 0
                        : MatchScore(methodDisplayName, methodFilter);
                    if (methodFilter is not null && methodScore < 0 && typeNameMatchScore < 0)
                    {
                        continue;
                    }

                    var score =
                        Math.Max(typeScore, 0)
                        + Math.Max(methodScore, 0)
                        + Math.Max(typeNameMatchScore, 0);
                    scored.Add((score, inScope, new BrowsedMethod(assembly, type, method)));
                }
            }
        }

        return
        [
            .. scored
                .OrderByDescending(entry => entry.InScope)
                .ThenByDescending(entry => entry.Score)
                .ThenBy(
                    entry => entry.Method.DeclaringType.FullName ?? entry.Method.DeclaringType.Name,
                    StringComparer.OrdinalIgnoreCase
                )
                .ThenBy(entry => entry.Method.Method.Name, StringComparer.OrdinalIgnoreCase)
                .Select(entry => entry.Method),
        ];
    }

    // Whether `type` (declared in `assembly`) falls under `scope`'s current tree position. Only
    // the deepest field scope sets is checked -- a default BrowseScope (nothing selected, e.g. the
    // "All assemblies" root) matches everything, so it never reorders results.
    private static bool IsInScope(Assembly assembly, Type type, BrowseScope scope) =>
        scope.TypeFullName is not null
            ? assembly.FullName == scope.AssemblyFullName && type.FullName == scope.TypeFullName
        : scope.Namespace is not null
            ? assembly.FullName == scope.AssemblyFullName
                && (type.Namespace ?? "") == scope.Namespace
        : scope.AssemblyFullName is null || assembly.FullName == scope.AssemblyFullName;

    // How well `filter` matches `candidate`, or -1 if it doesn't match at all (a plain
    // case-insensitive substring check, same as before). Among matches, an exact name match ranks
    // above matching just the last segment (e.g. "Pawn" against "Verse.Pawn"), which ranks above a
    // prefix match, which ranks above the filter starting right at a namespace/nested-type
    // separator or a camelCase word boundary (e.g. "Kill" inside "DoKillSideEffects"), which ranks
    // above the filter merely occurring somewhere in the middle of a word (e.g. "kill" inside
    // "DrawSkillSummaries", via "Skill").
    private static int MatchScore(string candidate, string filter)
    {
        if (filter.Length == 0)
        {
            return 0;
        }

        if (string.Equals(candidate, filter, StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }

        var lastSeparator = candidate.LastIndexOfAny(['.', '+']);
        var simpleName = lastSeparator >= 0 ? candidate[(lastSeparator + 1)..] : candidate;
        if (string.Equals(simpleName, filter, StringComparison.OrdinalIgnoreCase))
        {
            return 90;
        }

        if (simpleName.StartsWith(filter, StringComparison.OrdinalIgnoreCase))
        {
            return 70;
        }

        if (candidate.StartsWith(filter, StringComparison.OrdinalIgnoreCase))
        {
            return 60;
        }

        var index = candidate.IndexOf(filter, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return -1;
        }

        var atWordBoundary =
            candidate[index - 1] is '.' or '+' or '_'
            || (char.IsUpper(candidate[index]) && char.IsLower(candidate[index - 1]));
        return atWordBoundary ? 40 : 10;
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
        var parameters = string.Join(", ", method.GetParameters().Select(FormatParameter));
        if (method is ConstructorInfo)
        {
            // A type's static constructor and its parameterless instance constructor would
            // otherwise format identically (both "TypeName()") -- "static" is what tells them
            // apart in the picker.
            var prefix = method.IsStatic ? "static " : "";
            return $"{prefix}{method.DeclaringType?.Name ?? method.Name}({parameters})";
        }

        var returnTypeName = method is MethodInfo methodInfo
            ? FormatTypeName(methodInfo.ReturnType)
            : "Void";
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
    private static MethodBase[] GetLoadableMethods(Type type) =>
        MethodsCache.GetOrAdd(
            type,
            static t =>
            {
                try
                {
                    return
                    [
                        .. t.GetMethods(AllDeclaredMethods),
                        .. t.GetConstructors(AllDeclaredMethods),
                    ];
                }
                catch (Exception ex) when (ex is TypeLoadException or ReflectionTypeLoadException)
                {
                    return [];
                }
            }
        );
}
