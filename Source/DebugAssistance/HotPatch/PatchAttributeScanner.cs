namespace DebugAssistance.HotPatch;

// One [HarmonyPrefix]/[HarmonyPostfix]/[HarmonyTranspiler]/[HarmonyFinalizer]-attributed method
// found by Scan below, together with the target method its [HarmonyPatch] attribute(s) (class-
// level, method-level, or both, merged the same way Harmony itself merges them) resolve to.
internal sealed record DiscoveredPatch(
    MethodBase Target,
    MethodInfo PatchMethod,
    OnTheFlyPatchType PatchType
);

// Finds every Harmony-attributed patch method already annotated in a hot-patch assembly, so a
// project built from a previous scaffold (or hand-written the same way) can be offered to the
// player as a checklist of pre-configured target/patch method/patch type combinations right after
// loading, instead of requiring each one to be picked by hand through the target- and
// patch-method pickers. Mirrors the class+method attribute merge HarmonyLib's own
// PatchClassProcessor performs (HarmonyMethod.Merge of GetFromType then GetFromMethod), but
// PatchClassProcessor's own patchMethods/AttributePatch fields are internal to HarmonyLib and
// can't be reused directly from here.
internal static class PatchAttributeScanner
{
    internal static List<DiscoveredPatch> Scan(Assembly assembly)
    {
        List<DiscoveredPatch> results = [];
        foreach (var type in MethodBrowser.GetLoadableTypes(assembly))
        {
            var containerInfo = HarmonyMethod.Merge(HarmonyMethodExtensions.GetFromType(type));
            foreach (var method in AccessTools.GetDeclaredMethods(type))
            {
                if (DetectPatchType(method) is not { } patchType)
                {
                    continue;
                }

                var methodInfo = HarmonyMethod.Merge(HarmonyMethodExtensions.GetFromMethod(method));
                var merged = containerInfo.Merge(methodInfo);
                if (ResolveOriginalMethod(merged) is not { } target)
                {
                    continue;
                }

                results.Add(new DiscoveredPatch(target, method, patchType));
            }
        }
        return results;
    }

    private static OnTheFlyPatchType? DetectPatchType(MethodInfo method) =>
        method switch
        {
            _ when method.GetCustomAttribute<HarmonyPrefix>() is not null =>
                OnTheFlyPatchType.Prefix,
            _ when method.GetCustomAttribute<HarmonyPostfix>() is not null =>
                OnTheFlyPatchType.Postfix,
            _ when method.GetCustomAttribute<HarmonyTranspiler>() is not null =>
                OnTheFlyPatchType.Transpiler,
            _ when method.GetCustomAttribute<HarmonyFinalizer>() is not null =>
                OnTheFlyPatchType.Finalizer,
            _ => null,
        };

    // Only the two MethodType cases the target-method picker and ProjectScaffolder's own generated
    // [HarmonyPatch] attribute ever produce (Normal and Constructor) -- HarmonyLib's own
    // PatchTools.GetOriginalMethod handles many more (getters/setters/enumerators/operators/...),
    // but that method is internal to HarmonyLib and can't be called from here. A patch attributed
    // with one of those other MethodTypes, or with no declaring type at all, is silently skipped
    // rather than offered with a target we can't actually resolve.
    private static MethodBase? ResolveOriginalMethod(HarmonyMethod info) =>
        info switch
        {
            { declaringType: null } => null,
            { methodType: null or MethodType.Normal, methodName: null or "" } => null,
            { declaringType: { } declaringType, methodType: null or MethodType.Normal } =>
                AccessTools.DeclaredMethod(declaringType, info.methodName, info.argumentTypes),
            { declaringType: { } declaringType, methodType: MethodType.Constructor } =>
                AccessTools.DeclaredConstructor(declaringType, info.argumentTypes),
            _ => null,
        };
}
