using System.Collections.Specialized;
using System.Net;
using DebugAssistance.Decompilation;
using DebugAssistance.HotPatch;
using DebugAssistance.Web.Dtos;

namespace DebugAssistance.Web;

// /api/hotpatch/* routes wiring LiveAssemblyLoader/MethodBrowser/HotPatchManager into the web UI:
// load/reload a player-built assembly by path, browse methods on it or on every currently loaded
// assembly, apply/remove patches, and list the currently active ones. Every browse level
// (ServeAssemblyList/ServeNamespaceList/ServeTypeList/ServeMethodList) always drops a branch with
// no methods anywhere under it, since that's just dead weight in the tree; when the request also
// names a target method and a patch type, that same dropping additionally narrows to what
// PatchCompatibility.IsCompatible accepts, and each entry's type/method count reflects only
// whichever of those two counts currently applies.
internal static class HotPatchEndpoints
{
    internal static bool ServeLoadAssembly(HttpListenerContext ctx)
    {
        var body = ctx.Request.ReadJson<LoadAssemblyRequestDto>();
        if (body?.Path is not { } path || string.IsNullOrWhiteSpace(path))
        {
            return ctx.Response.WriteJsonError(400, "path is required");
        }

        // The game never unloads an old generation's Assembly instance, so leaving its patches
        // running after a reload is harmless -- unlike removing them, which can't be undone. So
        // only ask when there's actually something at stake: a previous load of this exact path
        // with patches still active against it, and the player hasn't already answered.
        var previous = DebugAssistanceMod.LiveAssemblyLoader.GetLoaded(path);
        var patchesFromPrevious = previous is { } prev
            ? DebugAssistanceMod.HotPatchManager.PatchesFromAssembly(prev.Assembly)
            : [];
        if (patchesFromPrevious.Count > 0 && body.RemoveOldPatches is null)
        {
            ctx.Response.WriteJson(
                new LoadAssemblyResultDto
                {
                    NeedsConfirmation = true,
                    PatchesFromPreviousLoadDescriptions =
                    [
                        .. patchesFromPrevious.Select(DescribePatch),
                    ],
                }
            );
            return true;
        }

        try
        {
            var loaded = DebugAssistanceMod.LiveAssemblyLoader.Load(path);
            var removedPatches =
                previous is { } prevToRemove && body.RemoveOldPatches == true
                    ? DebugAssistanceMod.HotPatchManager.RemoveAllFromAssembly(
                        prevToRemove.Assembly
                    )
                    : [];
            // FrameDecompiler.GetOrDecompile keys its cache on (assemblyLocation, metadataToken);
            // a rebuilt assembly at this same path commonly keeps the same tokens for unchanged
            // methods, so any entries decompiled from the previous load must be dropped now, or a
            // later decompile request would keep serving stale, pre-reload source.
            FrameDecompiler.InvalidateCacheForAssemblyLocation(path);
            ctx.Response.WriteJson(
                new LoadAssemblyResultDto
                {
                    NeedsConfirmation = false,
                    AssemblyName = loaded.Assembly.GetName().Name ?? loaded.Assembly.FullName,
                    AssemblyFullName = loaded.Assembly.FullName,
                    Generation = loaded.Generation,
                    RemovedPatchDescriptions = [.. removedPatches.Select(DescribePatch)],
                }
            );
            return true;
        }
        catch (Exception ex)
            when (ex is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            return ctx.Response.WriteJsonError(400, ex.Message);
        }
    }

    // GET /api/hotpatch/loaded-assemblies: every path already loaded this session, at its current
    // generation, so the patch-assembly picker can switch straight into a browsed method tree for
    // one of these without ever calling ServeLoadAssembly (and thus without risking its
    // reload-confirmation prompt).
    internal static bool ServeLoadedAssemblies(HttpListenerContext ctx)
    {
        var entries = DebugAssistanceMod
            .LiveAssemblyLoader.GetAllLoaded()
            .Select(entry => new LoadedAssemblyDto
            {
                Path = entry.Path,
                AssemblyName =
                    entry.Loaded.Assembly.GetName().Name ?? entry.Loaded.Assembly.FullName,
                Generation = entry.Loaded.Generation,
            })
            .OrderBy(entry => entry.AssemblyName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        ctx.Response.WriteJson(new LoadedAssemblyListDto { Assemblies = entries });
        return true;
    }

    internal static bool ServeMethodList(HttpListenerContext ctx)
    {
        var query = ctx.Request.QueryString;
        var path = query["path"];
        var filter = string.IsNullOrEmpty(query["filter"]) ? null : query["filter"];
        var assemblyFullName = query["assemblyFullName"];
        var typeFullName = query["typeFullName"];
        var compatibleWith = ResolveCompatibilityFilter(query);

        // The tree picker's leaf level: it already knows exactly which type it's listing members
        // of, so this takes precedence over the flat path/filter search below.
        if (!string.IsNullOrEmpty(typeFullName))
        {
            if (string.IsNullOrEmpty(assemblyFullName))
            {
                return ctx.Response.WriteJsonError(
                    400,
                    "assemblyFullName is required with typeFullName"
                );
            }

            var owningAssembly = ResolveAssemblyByFullName(assemblyFullName);
            var type = owningAssembly?.GetType(typeFullName);
            if (owningAssembly is null || type is null)
            {
                return ctx.Response.WriteJsonError(404, "Assembly or type not found");
            }

            var typeMethods = MethodBrowser.BrowseMethods(owningAssembly, type);
            if (compatibleWith is { } compat)
            {
                typeMethods = [.. typeMethods.Where(method => IsCompatible(method, compat))];
            }

            ctx.Response.WriteJson(
                new MethodListResponseDto { Methods = [.. typeMethods.Select(ToDto)] }
            );
            return true;
        }

        IEnumerable<Assembly> assemblies;
        if (string.IsNullOrEmpty(path))
        {
            assemblies = AppDomain.CurrentDomain.GetAssemblies();
        }
        else if (DebugAssistanceMod.LiveAssemblyLoader.GetLoaded(path) is { } loaded)
        {
            assemblies = [loaded.Assembly];
        }
        else
        {
            return ctx.Response.WriteJsonError(404, "Assembly not loaded");
        }

        var methods = MethodBrowser.Browse(assemblies, filter);
        if (compatibleWith is { } compatForSearch)
        {
            methods = [.. methods.Where(method => IsCompatible(method, compatForSearch))];
        }

        ctx.Response.WriteJson(new MethodListResponseDto { Methods = [.. methods.Select(ToDto)] });
        return true;
    }

    // Opportunistic: only filters when the picker already knows a target method and patch type
    // (targetAssemblyFullName/targetMetadataToken/patchType all present and resolvable). Missing
    // or malformed values fall back to no filtering rather than an error response, since the
    // patch-method picker can legitimately be browsed before a target method is chosen.
    internal static (MethodBase Target, HarmonyPatchType PatchType)? ResolveCompatibilityFilter(
        NameValueCollection query
    )
    {
        var targetAssemblyFullName = query["targetAssemblyFullName"];
        var targetMetadataTokenText = query["targetMetadataToken"];
        var patchTypeText = query["patchType"];
        if (
            string.IsNullOrEmpty(targetAssemblyFullName)
            || !int.TryParse(targetMetadataTokenText, out var metadataToken)
            || !TryParsePatchType(patchTypeText, out var patchType)
        )
        {
            return null;
        }

        var target = ResolveMethod(
            ResolveAssemblyByFullName(targetAssemblyFullName),
            metadataToken
        );
        return target is null ? null : (target, patchType);
    }

    private static bool IsCompatible(
        BrowsedMethod method,
        (MethodBase Target, HarmonyPatchType PatchType) compatibleWith
    ) =>
        PatchCompatibility.IsCompatible(
            compatibleWith.Target,
            (MethodInfo)method.Method,
            compatibleWith.PatchType
        );

    // How many of `type`'s own methods PatchCompatibility.IsCompatible accepts, or its plain
    // method count when `compatibleWith` is null -- shared by ServeTypeList's MethodCount and, via
    // HasCompatibleMethod below, by ServeNamespaceList/ServeAssemblyList's own type counts. Internal
    // (like ResolveMethod/ToDto/etc. above) so tests can exercise it without going through an
    // HttpListenerContext.
    internal static int CountMethods(
        Assembly assembly,
        Type type,
        (MethodBase Target, HarmonyPatchType PatchType)? compatibleWith
    )
    {
        var methods = MethodBrowser.BrowseMethods(assembly, type);
        return compatibleWith is not { } compat
            ? methods.Count
            : methods.Count(method => IsCompatible(method, compat));
    }

    // Whether `type` has at least one method at all, or -- once a target method and patch type are
    // given -- at least one PatchCompatibility.IsCompatible accepts. Either way, a type this
    // returns false for has nothing a player could ever pick from its method list.
    internal static bool HasCompatibleMethod(
        Assembly assembly,
        Type type,
        (MethodBase Target, HarmonyPatchType PatchType)? compatibleWith
    ) => CountMethods(assembly, type, compatibleWith) > 0;

    internal static int CountCompatibleTypes(
        Assembly assembly,
        (MethodBase Target, HarmonyPatchType PatchType)? compatibleWith
    ) =>
        MethodBrowser
            .GetLoadableTypes(assembly)
            .Count(type => HasCompatibleMethod(assembly, type, compatibleWith));

    // Root level of the tree-style picker: every currently loaded assembly (the target-method
    // picker), or just the one loaded from `path` (the patch-method picker) -- same path/omitted
    // convention as ServeMethodList above. An assembly with no (compatible) type anywhere in it is
    // dropped rather than left to lead to an empty namespace list.
    internal static bool ServeAssemblyList(HttpListenerContext ctx)
    {
        var query = ctx.Request.QueryString;
        var path = query["path"];
        var compatibleWith = ResolveCompatibilityFilter(query);

        IEnumerable<Assembly> assemblies;
        if (string.IsNullOrEmpty(path))
        {
            assemblies = AppDomain.CurrentDomain.GetAssemblies();
        }
        else if (DebugAssistanceMod.LiveAssemblyLoader.GetLoaded(path) is { } loaded)
        {
            assemblies = [loaded.Assembly];
        }
        else
        {
            return ctx.Response.WriteJsonError(404, "Assembly not loaded");
        }

        var entries = assemblies
            .Select(assembly => new AssemblyEntryDto
            {
                Name = assembly.GetName().Name ?? assembly.FullName,
                FullName = assembly.FullName,
                TypeCount = CountCompatibleTypes(assembly, compatibleWith),
            })
            .Where(entry => entry.TypeCount > 0)
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ctx.Response.WriteJson(new AssemblyListResponseDto { Assemblies = entries });
        return true;
    }

    // Second level: the namespaces declared by `assemblyFullName`'s own types. A namespace with no
    // type that has a (compatible) method is dropped, same reasoning as ServeAssemblyList above.
    internal static bool ServeNamespaceList(HttpListenerContext ctx)
    {
        var query = ctx.Request.QueryString;
        var assemblyFullName = query["assemblyFullName"];
        if (string.IsNullOrEmpty(assemblyFullName))
        {
            return ctx.Response.WriteJsonError(400, "assemblyFullName is required");
        }

        var assembly = ResolveAssemblyByFullName(assemblyFullName);
        if (assembly is null)
        {
            return ctx.Response.WriteJsonError(404, "Assembly not found");
        }

        var compatibleWith = ResolveCompatibilityFilter(query);
        var namespaces = MethodBrowser
            .BrowseNamespaces(assembly)
            .Select(ns => new NamespaceEntryDto
            {
                Name = ns.Name,
                TypeCount = MethodBrowser
                    .BrowseTypes(assembly, ns.Name)
                    .Count(type => HasCompatibleMethod(assembly, type, compatibleWith)),
            })
            .Where(ns => ns.TypeCount > 0)
            .ToList();
        ctx.Response.WriteJson(new NamespaceListResponseDto { Namespaces = namespaces });
        return true;
    }

    // Third level: the types declared directly under one namespace of `assemblyFullName`.
    // `namespace` is required but a present-and-empty value (the global namespace) is valid --
    // that's why this checks `is null` rather than IsNullOrEmpty. A type with no (compatible)
    // method of its own is dropped, same reasoning as the levels above.
    internal static bool ServeTypeList(HttpListenerContext ctx)
    {
        var query = ctx.Request.QueryString;
        var assemblyFullName = query["assemblyFullName"];
        var ns = query["namespace"];
        if (string.IsNullOrEmpty(assemblyFullName) || ns is null)
        {
            return ctx.Response.WriteJsonError(400, "assemblyFullName and namespace are required");
        }

        var assembly = ResolveAssemblyByFullName(assemblyFullName);
        if (assembly is null)
        {
            return ctx.Response.WriteJsonError(404, "Assembly not found");
        }

        var compatibleWith = ResolveCompatibilityFilter(query);
        var types = MethodBrowser
            .BrowseTypes(assembly, ns)
            .Select(type => new TypeEntryDto
            {
                Name = DisplayTypeName(type),
                FullName = type.FullName ?? type.Name,
                MethodCount = CountMethods(assembly, type, compatibleWith),
            })
            .Where(entry => entry.MethodCount > 0)
            .ToList();
        ctx.Response.WriteJson(new TypeListResponseDto { Types = types });
        return true;
    }

    // A nested type's own Type.Name is just its inner name ("Inner") -- prefixing it with its
    // containing type(s) ("Outer.Inner") avoids ambiguous-looking entries once nested types are
    // mixed in with top-level ones under the same namespace listing.
    internal static string DisplayTypeName(Type type)
    {
        var fullName = type.FullName ?? type.Name;
        var ns = type.Namespace;
        var withoutNamespace = ns is null ? fullName : fullName[(ns.Length + 1)..];
        return withoutNamespace.Replace('+', '.');
    }

    internal static bool ServeApplyPatch(HttpListenerContext ctx)
    {
        var body = ctx.Request.ReadJson<ApplyPatchRequestDto>();
        if (body is null)
        {
            return ctx.Response.WriteJsonError(400, "Request body is required");
        }

        if (body.Target is null || body.PatchMethod is null || body.SourceAssemblyPath is null)
        {
            return ctx.Response.WriteJsonError(
                400,
                "target, patchMethod, and sourceAssemblyPath are required"
            );
        }

        if (!TryParsePatchType(body.PatchType, out var patchType))
        {
            return ctx.Response.WriteJsonError(
                400,
                "patchType must be one of Prefix, Postfix, Transpiler, Finalizer"
            );
        }

        var loadedPatchAssembly = DebugAssistanceMod.LiveAssemblyLoader.GetLoaded(
            body.SourceAssemblyPath
        );
        if (loadedPatchAssembly is not { } loaded)
        {
            return ctx.Response.WriteJsonError(
                400,
                "sourceAssemblyPath is not currently loaded — load it first"
            );
        }

        var target = ResolveMethod(
            ResolveAssemblyByFullName(body.Target.AssemblyFullName),
            body.Target.MetadataToken
        );

        if (
            target is null
            || ResolveMethod(loaded.Assembly, body.PatchMethod.MetadataToken)
                is not MethodInfo patchMethod
        )
        {
            return ctx.Response.WriteJsonError(404, "Target or patch method could not be resolved");
        }

        var (patch, error) = DebugAssistanceMod.HotPatchManager.Apply(
            target,
            patchMethod,
            patchType,
            body.SourceAssemblyPath,
            loaded.Generation
        );

        ctx.Response.WriteJson(
            new ApplyPatchResultDto
            {
                Success = patch is not null,
                Error = error,
                Patch = patch is null ? null : ToDto(patch),
            }
        );
        return true;
    }

    internal static bool ServeRemovePatch(HttpListenerContext ctx, string idPart)
    {
        if (!Guid.TryParse(idPart, out var id))
        {
            return ctx.Response.WriteJsonError(400, "Invalid patch id");
        }

        var patch = DebugAssistanceMod.HotPatchManager.ActivePatches.FirstOrDefault(p =>
            p.Id == id
        );
        if (patch is null)
        {
            return ctx.Response.WriteJsonError(404, "Patch not found");
        }

        var removed = DebugAssistanceMod.HotPatchManager.Remove(patch);
        ctx.Response.WriteJson(new RemovePatchResultDto { Removed = removed });
        return true;
    }

    // POST /api/hotpatch/scaffold: reaches ProjectScaffolder from either hot-patch entry point --
    // Target is whichever method the panel's own target-method picker currently holds (pre-filled
    // from a frame, hand-picked, or left unset), never a separate selection of its own.
    internal static bool ServeScaffold(HttpListenerContext ctx)
    {
        var body = ctx.Request.ReadJson<ScaffoldRequestDto>();
        if (
            body is null
            || string.IsNullOrWhiteSpace(body.DirectoryPath)
            || string.IsNullOrWhiteSpace(body.ProjectName)
        )
        {
            return ctx.Response.WriteJsonError(400, "directoryPath and projectName are required");
        }

        var target = body.Target is { } targetRef
            ? ResolveMethod(
                ResolveAssemblyByFullName(targetRef.AssemblyFullName),
                targetRef.MetadataToken
            )
            : null;

        var result = ProjectScaffolder.Scaffold(
            body.DirectoryPath,
            body.ProjectName,
            target,
            context: null
        );
        ctx.Response.WriteJson(
            new ScaffoldResultDto
            {
                Success = result.Success,
                Error = result.Error,
                ProjectDirectory = result.ProjectDirectory,
                ExpectedAssemblyPath = result.ExpectedAssemblyPath,
            }
        );
        return true;
    }

    // GET /api/hotpatch/scaffold/suggested-name: the same ProjectScaffolder.SuggestProjectName used
    // by Scaffold itself, so the name shown to the player before they generate a project (and its
    // 40-character truncation) can never drift out of sync with what Scaffold will actually accept.
    internal static bool ServeSuggestScaffoldName(HttpListenerContext ctx)
    {
        var query = ctx.Request.QueryString;
        var assemblyFullName = query["assemblyFullName"];
        var metadataTokenText = query["metadataToken"];

        MethodBase? target = null;
        if (!string.IsNullOrEmpty(assemblyFullName) && !string.IsNullOrEmpty(metadataTokenText))
        {
            if (!int.TryParse(metadataTokenText, out var metadataToken))
            {
                return ctx.Response.WriteJsonError(400, "metadataToken must be an integer");
            }

            target = ResolveMethod(ResolveAssemblyByFullName(assemblyFullName), metadataToken);
        }

        ctx.Response.WriteJson(
            new SuggestedProjectNameDto
            {
                ProjectName = ProjectScaffolder.SuggestProjectName(target, context: null),
            }
        );
        return true;
    }

    internal static bool ServeActivePatches(HttpListenerContext ctx)
    {
        ctx.Response.WriteJson(
            new ActivePatchListDto
            {
                Patches = [.. DebugAssistanceMod.HotPatchManager.ActivePatches.Select(ToDto)],
            }
        );
        return true;
    }

    internal static bool TryParsePatchType(string? value, out HarmonyPatchType patchType)
    {
        switch (value)
        {
            case nameof(HarmonyPatchType.Prefix):
                patchType = HarmonyPatchType.Prefix;
                return true;
            case nameof(HarmonyPatchType.Postfix):
                patchType = HarmonyPatchType.Postfix;
                return true;
            case nameof(HarmonyPatchType.Transpiler):
                patchType = HarmonyPatchType.Transpiler;
                return true;
            case nameof(HarmonyPatchType.Finalizer):
                patchType = HarmonyPatchType.Finalizer;
                return true;
            default:
                patchType = default;
                return false;
        }
    }

    // MetadataToken is only meaningful together with the exact Module instance it came from — see
    // HotPatchDtos.MethodRefDto's remarks on why the patch method is resolved through
    // LiveAssemblyLoader instead of this, but the target method (never reloaded mid-session in
    // practice) goes through here.
    internal static MethodBase? ResolveMethod(Assembly? assembly, int metadataToken)
    {
        if (assembly is null)
        {
            return null;
        }

        try
        {
            return assembly.ManifestModule.ResolveMethod(metadataToken);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    // The classic .NET/Mono runtime RimWorld embeds never unloads an Assembly instance, so after a
    // reload the previous load of the same name can still be sitting in the AppDomain's assembly
    // list alongside the new one — AppDomain.GetAssemblies() appends new assemblies at the end, so
    // the last match is the one currently in use.
    internal static Assembly? ResolveAssemblyByFullName(string assemblyFullName) =>
        AppDomain.CurrentDomain.GetAssemblies().LastOrDefault(a => a.FullName == assemblyFullName);

    internal static BrowsedMethodDto ToDto(BrowsedMethod method) =>
        new()
        {
            AssemblyName = method.Assembly.GetName().Name ?? method.Assembly.FullName,
            AssemblyFullName = method.Assembly.FullName,
            MetadataToken = method.Method.MetadataToken,
            DeclaringTypeName = method.DeclaringType.FullName ?? method.DeclaringType.Name,
            Namespace = method.DeclaringType.Namespace ?? "",
            MethodName = method.Method.Name,
            Signature = MethodBrowser.FormatSignature(method.Method),
            IsStatic = method.Method.IsStatic,
        };

    internal static ActivePatchDto ToDto(OnTheFlyPatch patch)
    {
        var patchAssembly = patch.PatchMethod.Module.Assembly;
        return new ActivePatchDto
        {
            Id = patch.Id.ToString(),
            TargetDescription = DescribeMethod(patch.Target),
            PatchMethodDescription = DescribeMethod(patch.PatchMethod),
            PatchType = patch.PatchType.ToString(),
            SourceAssemblyPath = patch.SourceAssemblyPath,
            SourceAssemblyName = patchAssembly.GetName().Name ?? patchAssembly.FullName,
            SourceAssemblyGeneration = patch.SourceAssemblyGeneration,
        };
    }

    internal static string DescribePatch(OnTheFlyPatch patch) =>
        $"{patch.PatchType} on {DescribeMethod(patch.Target)}";

    internal static string DescribeMethod(MethodBase method) =>
        CSharpTypeFormatter.DescribeMethod(method);
}
