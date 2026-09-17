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

        HashSet<Guid>? removePatchIds = null;
        if (body.RemovePatchIds is { } requestedIds)
        {
            removePatchIds = [];
            foreach (var idText in requestedIds)
            {
                if (!Guid.TryParse(idText, out var id))
                {
                    return ctx.Response.WriteJsonError(400, $"Invalid patch id: {idText}");
                }
                _ = removePatchIds.Add(id);
            }
        }

        // The game never unloads an old generation's Assembly instance, so leaving its patches
        // running after a reload is harmless -- unlike removing them, which can't be undone. So
        // only ask when there's actually something at stake: a previous load of this exact path
        // with patches still active against it, and the player hasn't already answered.
        var previous = DebugAssistanceMod.LiveAssemblyLoader.GetLoaded(path);
        var patchesFromPrevious = previous is { } prev
            ? DebugAssistanceMod.HotPatchManager.PatchesFromAssembly(prev.Assembly)
            : [];
        if (patchesFromPrevious.Count > 0 && removePatchIds is null)
        {
            ctx.Response.WriteJson(
                new LoadAssemblyResultDto
                {
                    NeedsConfirmation = true,
                    PatchesFromPreviousLoad = [.. patchesFromPrevious.Select(ToDto)],
                }
            );
            return true;
        }

        try
        {
            var loaded = DebugAssistanceMod.LiveAssemblyLoader.Load(path);
            var removedPatches = removePatchIds is { Count: > 0 }
                ? DebugAssistanceMod.HotPatchManager.RemoveMany(removePatchIds)
                : [];
            // FrameDecompiler.GetOrDecompile keys its cache on (assemblyLocation, metadataToken);
            // a rebuilt assembly at this same path commonly keeps the same tokens for unchanged
            // methods, so any entries decompiled from the previous load must be dropped now, or a
            // later decompile request would keep serving stale, pre-reload source.
            FrameDecompiler.InvalidateCacheForAssemblyLocation(path);
            // A reload can add, remove, or change the methods a previously cached search would
            // have found, both for a search scoped to this path and for a path-less search across
            // every loaded assembly, so the whole search cache is dropped rather than just this
            // path's entries.
            MethodSearchCache.Clear();
            var discoveredPatches = PatchAttributeScanner.Scan(loaded.Assembly);
            ctx.Response.WriteJson(
                new LoadAssemblyResultDto
                {
                    NeedsConfirmation = false,
                    AssemblyName = loaded.Assembly.GetName().Name ?? loaded.Assembly.FullName,
                    AssemblyFullName = loaded.Assembly.FullName,
                    Generation = loaded.Generation,
                    RemovedPatchDescriptions = [.. removedPatches.Select(DescribePatch)],
                    DiscoveredPatches =
                        discoveredPatches.Count > 0 ? [.. discoveredPatches.Select(ToDto)] : null,
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
                new MethodListResponseDto
                {
                    Methods = [.. typeMethods.Select(ToDto)],
                    TotalCount = typeMethods.Count,
                    HasMore = false,
                }
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

        if (!int.TryParse(query["offset"], out var offset) || offset < 0)
        {
            offset = 0;
        }

        var cacheKey = BuildSearchCacheKey(path, filter, compatibleWith);
        var methods = MethodSearchCache.TryGet(
            cacheKey,
            DebugAssistanceMod.Settings.SearchCacheTtlSeconds
        );
        if (methods is null)
        {
            methods = MethodBrowser.Browse(assemblies, filter);
            if (compatibleWith is { } compatForSearch)
            {
                methods = [.. methods.Where(method => IsCompatible(method, compatForSearch))];
            }
            MethodSearchCache.Set(
                cacheKey,
                methods,
                DebugAssistanceMod.Settings.SearchCacheTtlSeconds,
                DebugAssistanceMod.Settings.SearchCacheMaxEntries
            );
        }

        var page = methods.Skip(offset).Take(SearchPageSize).ToList();
        ctx.Response.WriteJson(
            new MethodListResponseDto
            {
                Methods = [.. page.Select(ToDto)],
                TotalCount = methods.Count,
                HasMore = offset + page.Count < methods.Count,
            }
        );
        return true;
    }

    // Full results for a flat search are always computed and cached (see MethodSearchCache), but
    // only this many are ever serialized into one response -- a search that matches hundreds of
    // methods would otherwise produce a multi-megabyte response the frontend has to parse and
    // render all at once. The frontend requests further pages via the `offset` query parameter.
    internal const int SearchPageSize = 10;

    // The cache key is just the search's own parameters -- an identical search (same path, filter,
    // and compatibility target/patch type) always maps to the same key, so re-issuing it (e.g.
    // paging, or typing "foo" -> "foobar" -> back to "foo") reuses the cached result for as long as
    // it stays in MethodSearchCache. '\0' can't appear in any of these parts, so it's safe as a
    // separator between them.
    internal static string BuildSearchCacheKey(
        string? path,
        string? filter,
        (MethodBase Target, OnTheFlyPatchType PatchType)? compatibleWith
    )
    {
        var compatPart = compatibleWith is { } compat
            ? $"{compat.Target.Module.Assembly.FullName}\0{compat.Target.MetadataToken}\0{compat.PatchType}"
            : "";
        return $"{path}\0{filter}\0{compatPart}";
    }

    // Opportunistic: only filters when the picker already knows a target method and patch type
    // (targetAssemblyFullName/targetMetadataToken/patchType all present and resolvable). Missing
    // or malformed values fall back to no filtering rather than an error response, since the
    // patch-method picker can legitimately be browsed before a target method is chosen.
    internal static (MethodBase Target, OnTheFlyPatchType PatchType)? ResolveCompatibilityFilter(
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

    // A constructor can never serve as a Prefix/Postfix/Transpiler/Finalizer -- Harmony patch
    // methods must be ordinary MethodInfos -- so it's never compatible here, regardless of
    // PatchType. This only matters for the patch-method picker (browsing the player's own
    // assembly for candidates); the target-method picker never sets compatibleWith at all, so a
    // constructor being a valid patch *target* is unaffected.
    private static bool IsCompatible(
        BrowsedMethod method,
        (MethodBase Target, OnTheFlyPatchType PatchType) compatibleWith
    ) =>
        method.Method is MethodInfo candidate
        && PatchCompatibility.IsCompatible(
            compatibleWith.Target,
            candidate,
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
        (MethodBase Target, OnTheFlyPatchType PatchType)? compatibleWith
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
        (MethodBase Target, OnTheFlyPatchType PatchType)? compatibleWith
    ) => CountMethods(assembly, type, compatibleWith) > 0;

    internal static int CountCompatibleTypes(
        Assembly assembly,
        (MethodBase Target, OnTheFlyPatchType PatchType)? compatibleWith
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

        if (body.Target is null || body.PatchMethod is null)
        {
            return ctx.Response.WriteJsonError(400, "target and patchMethod are required");
        }

        if (!TryParsePatchType(body.PatchType, out var patchType))
        {
            return ctx.Response.WriteJsonError(
                400,
                "patchType must be one of Prefix, Postfix, Transpiler, Finalizer, Replace"
            );
        }

        var target = ResolveMethod(
            ResolveAssemblyByFullName(body.Target.AssemblyFullName),
            body.Target.MetadataToken
        );
        if (target is null)
        {
            return ctx.Response.WriteJsonError(404, "Target method could not be resolved");
        }

        string sourceAssemblyPath;
        int sourceAssemblyGeneration;
        MethodInfo patchMethod;
        if (body.SourceAssemblyPath is { } requestedSourceAssemblyPath)
        {
            var loadedPatchAssembly = DebugAssistanceMod.LiveAssemblyLoader.GetLoaded(
                requestedSourceAssemblyPath
            );
            if (loadedPatchAssembly is not { } loaded)
            {
                return ctx.Response.WriteJsonError(
                    400,
                    "sourceAssemblyPath is not currently loaded — load it first"
                );
            }
            if (
                ResolveMethod(loaded.Assembly, body.PatchMethod.MetadataToken)
                is not MethodInfo resolvedPatchMethod
            )
            {
                return ctx.Response.WriteJsonError(404, "Patch method could not be resolved");
            }

            sourceAssemblyPath = requestedSourceAssemblyPath;
            sourceAssemblyGeneration = loaded.Generation;
            patchMethod = resolvedPatchMethod;
        }
        // No SourceAssemblyPath: a convenience patch, whose patch method already lives in an
        // assembly resolvable straight off the AppDomain -- DebugAssistance itself, a running mod,
        // or a loaded hot-patch assembly -- the same way the target method above always is.
        else
        {
            var patchMethodAssembly = ResolveAssemblyByFullName(body.PatchMethod.AssemblyFullName);
            if (
                ResolveMethod(patchMethodAssembly, body.PatchMethod.MetadataToken)
                is not MethodInfo resolvedPatchMethod
            )
            {
                return ctx.Response.WriteJsonError(404, "Patch method could not be resolved");
            }

            sourceAssemblyPath = patchMethodAssembly!.Location;
            sourceAssemblyGeneration = 0;
            patchMethod = resolvedPatchMethod;
        }

        var (patch, error) = DebugAssistanceMod.HotPatchManager.Apply(
            target,
            patchMethod,
            patchType,
            sourceAssemblyPath,
            sourceAssemblyGeneration
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

    // GET /api/hotpatch/convenience-patches: every [ConveniencePatch]-attributed method
    // ConveniencePatchRegistry currently knows about, optionally narrowed to the ones compatible
    // with a given target method (targetAssemblyFullName/targetMetadataToken) the same way the
    // patch-method picker narrows to PatchCompatibility.IsCompatible -- each entry checked against
    // its own PatchType rather than one shared across the whole list, since (unlike the
    // patch-method picker) every convenience patch already has its own fixed patch type.
    internal static bool ServeConveniencePatches(HttpListenerContext ctx)
    {
        var target = ResolveConveniencePatchTargetFilter(ctx.Request.QueryString);
        var patches = DebugAssistanceMod.ConveniencePatchRegistry.All;
        if (target is { } t)
        {
            patches = [.. patches.Where(patch => IsCompatible(t, patch))];
        }

        ctx.Response.WriteJson(
            new ConveniencePatchListDto { Patches = [.. patches.Select(ToDto)] }
        );
        return true;
    }

    // POST /api/hotpatch/convenience-patches/rescan: the hot-patch panel's "Rescan" button --
    // re-scans DebugAssistance's own built-ins, every running mod, and every loaded hot-patch
    // assembly, then responds with the same shape ServeConveniencePatches does (optionally
    // target-filtered the same way) so the panel can just replace its list with the response.
    internal static bool ServeRescanConveniencePatches(HttpListenerContext ctx)
    {
        DebugAssistanceMod.ConveniencePatchRegistry.Rescan();
        return ServeConveniencePatches(ctx);
    }

    private static bool IsCompatible(MethodBase target, ConveniencePatch patch) =>
        PatchCompatibility.IsCompatible(target, patch.PatchMethod, patch.PatchType);

    private static MethodBase? ResolveConveniencePatchTargetFilter(NameValueCollection query)
    {
        var targetAssemblyFullName = query["targetAssemblyFullName"];
        var targetMetadataTokenText = query["targetMetadataToken"];
        return
            string.IsNullOrEmpty(targetAssemblyFullName)
            || !int.TryParse(targetMetadataTokenText, out var metadataToken)
            ? null
            : ResolveMethod(ResolveAssemblyByFullName(targetAssemblyFullName), metadataToken);
    }

    // POST /api/hotpatch/remove-many: the "Remove" action in the active-patches list, for both a
    // single patch and a multi-select -- the RemovePatchesDialog always calls this, with one id or
    // several. Unknown or already-inactive ids are silently skipped rather than erroring the whole
    // request -- the player picked from a snapshot of ActivePatches that could have gone stale by
    // the time they confirmed, and RemovedIds tells the UI exactly what actually went away.
    internal static bool ServeRemoveManyPatches(HttpListenerContext ctx)
    {
        var body = ctx.Request.ReadJson<RemoveManyPatchesRequestDto>();
        if (body?.Ids is null)
        {
            return ctx.Response.WriteJsonError(400, "ids is required");
        }

        var ids = new HashSet<Guid>();
        foreach (var idText in body.Ids)
        {
            if (!Guid.TryParse(idText, out var id))
            {
                return ctx.Response.WriteJsonError(400, $"Invalid patch id: {idText}");
            }
            _ = ids.Add(id);
        }

        var removed = DebugAssistanceMod.HotPatchManager.RemoveMany(ids);
        ctx.Response.WriteJson(
            new RemoveManyPatchesResultDto
            {
                RemovedIds = [.. removed.Select(patch => patch.Id.ToString())],
            }
        );
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

    internal static bool TryParsePatchType(string? value, out OnTheFlyPatchType patchType)
    {
        switch (value)
        {
            case nameof(OnTheFlyPatchType.Prefix):
                patchType = OnTheFlyPatchType.Prefix;
                return true;
            case nameof(OnTheFlyPatchType.Postfix):
                patchType = OnTheFlyPatchType.Postfix;
                return true;
            case nameof(OnTheFlyPatchType.Transpiler):
                patchType = OnTheFlyPatchType.Transpiler;
                return true;
            case nameof(OnTheFlyPatchType.Finalizer):
                patchType = OnTheFlyPatchType.Finalizer;
                return true;
            case nameof(OnTheFlyPatchType.Replace):
                patchType = OnTheFlyPatchType.Replace;
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

    internal static DiscoveredPatchDto ToDto(DiscoveredPatch discovered) =>
        new()
        {
            Target = new MethodRefDto
            {
                AssemblyFullName = discovered.Target.Module.Assembly.FullName,
                MetadataToken = discovered.Target.MetadataToken,
            },
            TargetDescription = DescribeMethod(discovered.Target),
            PatchMethod = new MethodRefDto
            {
                AssemblyFullName = discovered.PatchMethod.Module.Assembly.FullName,
                MetadataToken = discovered.PatchMethod.MetadataToken,
            },
            PatchMethodDescription = DescribeMethod(discovered.PatchMethod),
            PatchType = discovered.PatchType.ToString(),
        };

    internal static ConveniencePatchDto ToDto(ConveniencePatch patch)
    {
        var patchAssembly = patch.PatchMethod.Module.Assembly;
        return new ConveniencePatchDto
        {
            Name = patch.Name,
            Description = patch.Description,
            PatchType = patch.PatchType.ToString(),
            PatchMethod = new MethodRefDto
            {
                AssemblyFullName = patchAssembly.FullName,
                MetadataToken = patch.PatchMethod.MetadataToken,
            },
            PatchMethodDescription = DescribeMethod(patch.PatchMethod),
            SourceAssemblyName = patchAssembly.GetName().Name ?? patchAssembly.FullName,
        };
    }

    internal static string DescribePatch(OnTheFlyPatch patch) =>
        $"{patch.PatchType} on {DescribeMethod(patch.Target)}";

    internal static string DescribeMethod(MethodBase method) =>
        CSharpTypeFormatter.DescribeMethod(method);
}
