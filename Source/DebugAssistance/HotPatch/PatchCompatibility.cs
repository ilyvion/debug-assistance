using System.Reflection.Emit;

namespace DebugAssistance.HotPatch;

// A static approximation of the signature rules Harmony's own patcher enforces at apply time --
// used to pre-filter the patch-method picker (HotPatchEndpoints.ServeMethodList) so obviously
// incompatible candidates never show up as choices for a given target method + patch type.
// HotPatchManager.Apply's real Harmony.Patch(...) call remains the actual authority; this only
// narrows the picker's list, it never blocks Apply on its own. Reuses Harmony's own special
// parameter name constants (HarmonyLib.MethodPatcherTools, exposed public by this project's
// `<Publicize Include="0Harmony" />`) rather than duplicating them as string literals, but doesn't
// attempt full parity with Harmony's real parameter binder -- notably, `[HarmonyArgument]`
// renaming is not accounted for.
internal static class PatchCompatibility
{
    internal static bool IsCompatible(
        MethodBase target,
        MethodInfo candidate,
        HarmonyPatchType patchType
    ) =>
        candidate.IsStatic
        && (
            patchType == HarmonyPatchType.Transpiler
                ? IsTranspilerCompatible(candidate)
                : IsFixReturnTypeCompatible(target, candidate, patchType)
                    && candidate
                        .GetParameters()
                        .All(parameter => IsFixParameterCompatible(target, parameter))
        );

    private static bool IsTranspilerCompatible(MethodInfo candidate) =>
        candidate.ReturnType == typeof(IEnumerable<CodeInstruction>)
        && candidate
            .GetParameters()
            .All(parameter =>
                parameter.ParameterType == typeof(IEnumerable<CodeInstruction>)
                || parameter.ParameterType == typeof(ILGenerator)
                || parameter.ParameterType == typeof(MethodBase)
            );

    private static bool IsFixReturnTypeCompatible(
        MethodBase target,
        MethodInfo candidate,
        HarmonyPatchType patchType
    ) =>
        candidate.ReturnType == typeof(void)
        || patchType switch
        {
            HarmonyPatchType.Prefix => candidate.ReturnType == typeof(bool),
            HarmonyPatchType.Postfix => TargetReturnType(target) is { } returnType
                && returnType != typeof(void)
                && IsCompatibleType(candidate.ReturnType, returnType),
            HarmonyPatchType.Finalizer => typeof(Exception).IsAssignableFrom(candidate.ReturnType),
            HarmonyPatchType.Transpiler
            or HarmonyPatchType.All
            or HarmonyPatchType.ReversePatch
            or HarmonyPatchType.InnerPrefix
            or HarmonyPatchType.InnerPostfix => true,
            _ => true,
        };

    private static bool IsFixParameterCompatible(MethodBase target, ParameterInfo parameter)
    {
        var name = parameter.Name;
        var type = ElementType(parameter.ParameterType);

        if (name == MethodPatcherTools.INSTANCE_PARAM)
        {
            return !target.IsStatic
                && target.DeclaringType is { } declaringType
                && IsCompatibleType(type, declaringType);
        }

        if (name is MethodPatcherTools.RESULT_VAR or MethodPatcherTools.RESULT_REF_VAR)
        {
            return TargetReturnType(target) is { } returnType
                && returnType != typeof(void)
                && IsCompatibleType(type, returnType);
        }

        if (
            name
            is MethodPatcherTools.STATE_VAR
                or MethodPatcherTools.ORIGINAL_METHOD_PARAM
                or MethodPatcherTools.RUN_ORIGINAL_VAR
                or MethodPatcherTools.ARGS_ARRAY_VAR
                or MethodPatcherTools.EXCEPTION_VAR
        )
        {
            // Always accepted -- __state/__args are patch-author-chosen types with nothing on the
            // target to check against, __originalMethod/__runOriginal have one conventional type
            // that's rarely worth policing, and __exception is only meaningful on a Finalizer but
            // showing up elsewhere is exactly the kind of Harmony-throws-at-apply-time mismatch
            // this filter isn't trying to fully replace.
            return true;
        }

        if (
            name?.StartsWith(MethodPatcherTools.INSTANCE_FIELD_PREFIX, StringComparison.Ordinal)
            == true
        )
        {
            var fieldName = name[MethodPatcherTools.INSTANCE_FIELD_PREFIX.Length..];
            var field = target.DeclaringType?.GetField(
                fieldName,
                BindingFlags.Public
                    | BindingFlags.NonPublic
                    | BindingFlags.Instance
                    | BindingFlags.Static
            );
            return field is not null && IsCompatibleType(type, field.FieldType);
        }

        return FindTargetParameter(target, name) is { } targetParameter
            && IsCompatibleType(type, targetParameter.ParameterType);
    }

    // Original parameters are matched by exact name, or -- per Harmony's own PARAM_INDEX_PREFIX
    // convention -- by position via a "__0", "__1", ... name when no name matches.
    private static ParameterInfo? FindTargetParameter(MethodBase target, string? name)
    {
        if (name is null)
        {
            return null;
        }

        var targetParameters = target.GetParameters();
        return targetParameters.FirstOrDefault(parameter => parameter.Name == name)
            ?? (
                name.StartsWith(MethodPatcherTools.PARAM_INDEX_PREFIX, StringComparison.Ordinal)
                && int.TryParse(name[MethodPatcherTools.PARAM_INDEX_PREFIX.Length..], out var index)
                && index >= 0
                && index < targetParameters.Length
                    ? targetParameters[index]
                    : null
            );
    }

    private static Type? TargetReturnType(MethodBase target) =>
        target is MethodInfo info ? info.ReturnType : null;

    private static Type ElementType(Type type) => type.IsByRef ? type.GetElementType() : type;

    // Permissive either-direction assignability check (plus an `object` escape hatch) rather than
    // exact type equality -- this filter only needs to catch obviously wrong candidates, not
    // reproduce Harmony's exact coercion rules.
    private static bool IsCompatibleType(Type candidateType, Type targetType) =>
        candidateType == typeof(object)
        || candidateType.IsAssignableFrom(targetType)
        || targetType.IsAssignableFrom(candidateType);
}
