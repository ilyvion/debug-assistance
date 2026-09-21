namespace DebugAssistance.HotPatch;

// One [ConveniencePatch]-attributed static method found by Scan below: a ready-made Harmony patch
// body offered against whatever target method the player currently has selected in the hot-patch
// panel, unlike PatchAttributeScanner's DiscoveredPatch, which already carries its own resolved
// [HarmonyPatch] target.
internal sealed record ConveniencePatch(
    string Name,
    string Description,
    OnTheFlyPatchType PatchType,
    MethodInfo PatchMethod
);

// Finds every [ConveniencePatch]-attributed method across a set of assemblies -- see
// ConveniencePatchRegistry for which assemblies that is (DebugAssistance's own built-ins, every
// running mod, and any loaded hot-patch assembly).
internal static class ConveniencePatchScanner
{
    internal static List<ConveniencePatch> Scan(IEnumerable<Assembly> assemblies)
    {
        List<ConveniencePatch> results = [];
        foreach (var assembly in assemblies)
        {
            foreach (var type in MethodBrowser.GetLoadableTypes(assembly))
            {
                foreach (var method in AccessTools.GetDeclaredMethods(type))
                {
                    if (TryGetAttribute(method) is not { } attribute)
                    {
                        continue;
                    }

                    results.Add(
                        new ConveniencePatch(
                            attribute.Name,
                            attribute.Description,
                            ToOnTheFlyPatchType(attribute.PatchType),
                            method
                        )
                    );
                }
            }
        }
        return results;
    }

    // Other mods' methods can carry attributes whose types fail to resolve (e.g. an attribute from
    // an optional or mismatched dependency); reading those throws, and such methods can't be
    // convenience patches.
    private static ConveniencePatchAttribute? TryGetAttribute(MethodInfo method)
    {
        try
        {
            return method.GetCustomAttribute<ConveniencePatchAttribute>();
        }
        catch (Exception ex)
        {
            Log.Warning(
                $"[DebugAssistance] Skipping {method.DeclaringType}.{method.Name} while scanning for convenience patches, its attributes could not be read: {ex}"
            );
            return null;
        }
    }

    private static OnTheFlyPatchType ToOnTheFlyPatchType(ConveniencePatchType patchType) =>
        patchType switch
        {
            ConveniencePatchType.Prefix => OnTheFlyPatchType.Prefix,
            ConveniencePatchType.Postfix => OnTheFlyPatchType.Postfix,
            ConveniencePatchType.Finalizer => OnTheFlyPatchType.Finalizer,
            _ => throw new ArgumentOutOfRangeException(nameof(patchType), patchType, null),
        };
}
