using System.Reflection.Emit;

namespace DebugAssistance.HotPatch;

// Builds the destructive-prefix shim behind a "Replace" on-the-fly patch: a dynamically generated
// static Harmony prefix that calls the player's replacement method in place of the target's own
// body, copies its return value into Harmony's __result convention, and unconditionally returns
// false so the target's own body never runs.
//
// For an instance target, the replacement method's own IL body is copied into an equivalent
// static method (the implicit `this` becoming a leading `object` parameter) via Harmony's own
// reverse-patch machinery (Harmony.CreateReversePatcher, backed by MethodCopier) rather than
// hand-rolled IL cloning. That's needed because the replacement always comes from a
// LiveAssemblyLoader-loaded assembly, which is always a distinct Type from the target's declaring
// type (reloading never reuses a previous generation's identity), so an ordinary managed call
// can't pass the target's own instance to the replacement's instance method directly. The copied
// IL still references the replacement's own field/member tokens, so it depends on the target's and
// replacement's declaring types having identical field layouts — true for the intended use case (a
// method body fix rebuilt from the same mod source) but not verified here.
internal static class ReplacePatchBuilder
{
    // Harmony refuses to reference a bare DynamicMethod as a patch method directly ("Cannot
    // directly reference dynamic method ... in Harmony. Use a factory method instead that will
    // return the dynamic method") -- the sanctioned way to hand it one is a real, compiled static
    // method with return type DynamicMethod (or MethodInfo) and a single MethodBase parameter,
    // which Harmony invokes with the target method and uses the result as the actual patch
    // (HarmonyLib.Patch.GetMethod). ResolveShim is that factory, shared across every Replace patch
    // this builder produces; _pendingShims is how Build hands it the actual generated shim for a
    // given target just before Harmony.Patch synchronously resolves it.
    private static readonly Dictionary<MethodBase, DynamicMethod> PendingShims = [];

    private static DynamicMethod ResolveShim(MethodBase original) => PendingShims[original];

    private static readonly MethodInfo Factory = AccessTools.Method(
        typeof(ReplacePatchBuilder),
        nameof(ResolveShim)
    );

    // The shim's IL calls straight into `core` via a bare `call` instruction rather than a
    // delegate, so nothing about that call keeps `core`'s managing DynamicMethod object alive on
    // its own -- and once that object is collected, its JIT-compiled native code can be freed out
    // from under the shim, which then jumps into unmapped memory the next time the target method
    // runs. Keeping a strong reference here for as long as the shim itself is retained closes that
    // gap.
    private static readonly Dictionary<MethodBase, MethodInfo> RetainedCores = [];

    internal static (MethodInfo? Shim, string? Error) Build(
        Harmony harmony,
        MethodBase target,
        MethodInfo replacement
    )
    {
        if (!IsCompatible(target, replacement, out var incompatibleReason))
        {
            return (null, incompatibleReason);
        }

        var targetParameters = target.GetParameters();
        var targetReturnType = TargetReturnType(target);

        MethodInfo core;
        if (replacement.IsStatic)
        {
            core = replacement;
        }
        else
        {
            var (built, error) = BuildStaticCore(
                harmony,
                replacement,
                targetParameters,
                targetReturnType
            );
            if (built is null)
            {
                return (null, error);
            }
            core = built;
        }

        RetainedCores[target] = core;
        PendingShims[target] = BuildPrefix(target, core, targetParameters, targetReturnType);
        return (Factory, null);
    }

    // Drops the shim (and its retained core) registered for `target` once its Replace patch is
    // actually removed, so neither dictionary keeps every past Replace target's generated methods
    // alive for the rest of the session.
    internal static void Forget(MethodBase target)
    {
        _ = PendingShims.Remove(target);
        _ = RetainedCores.Remove(target);
    }

    // The same exact-match rule PatchCompatibility.IsReplaceCompatible pre-filters the picker
    // with, re-checked here defensively since PatchCompatibility only narrows the picker's list —
    // it's never the sole authority on what Apply accepts.
    internal static bool IsCompatible(MethodBase target, MethodInfo replacement, out string? reason)
    {
        if (IsOpenGeneric(target) || IsOpenGeneric(replacement))
        {
            reason = "Replace does not support generic methods or methods on generic types.";
            return false;
        }

        if (target.IsStatic != replacement.IsStatic)
        {
            reason =
                "The replacement method must be static if and only if the target method is static.";
            return false;
        }

        var targetParameters = target.GetParameters();
        var replacementParameters = replacement.GetParameters();
        if (targetParameters.Length != replacementParameters.Length)
        {
            reason =
                "The replacement method must have the same number of parameters as the target method.";
            return false;
        }

        for (var i = 0; i < targetParameters.Length; i++)
        {
            if (targetParameters[i].ParameterType != replacementParameters[i].ParameterType)
            {
                reason =
                    $"Parameter {i} of the replacement method ({replacementParameters[i].ParameterType}) doesn't match the target method's ({targetParameters[i].ParameterType}).";
                return false;
            }
        }

        var targetReturnType = TargetReturnType(target);
        if (replacement.ReturnType != targetReturnType)
        {
            reason =
                $"The replacement method's return type ({replacement.ReturnType}) doesn't match the target method's ({targetReturnType}).";
            return false;
        }

        reason = null;
        return true;
    }

    private static bool IsOpenGeneric(MethodBase method) =>
        method.IsGenericMethod || method.DeclaringType?.IsGenericType == true;

    private static Type TargetReturnType(MethodBase target) =>
        target is MethodInfo info ? info.ReturnType : typeof(void);

    // Copies `replacement`'s own IL body into a static method with a leading `object` parameter
    // standing in for `this`, via the same reverse-patch mechanism HarmonyReversePatch uses --
    // built for exactly this "turn an instance method's body into an equivalent static one"
    // problem, so there's no need to hand-roll IL cloning or metadata token remapping here.
    private static (MethodInfo? Core, string? Error) BuildStaticCore(
        Harmony harmony,
        MethodInfo replacement,
        ParameterInfo[] targetParameters,
        Type targetReturnType
    )
    {
        var standinParameterTypes = new Type[targetParameters.Length + 1];
        standinParameterTypes[0] = typeof(object);
        for (var i = 0; i < targetParameters.Length; i++)
        {
            standinParameterTypes[i + 1] = targetParameters[i].ParameterType;
        }

        var standin = new DynamicMethod(
            $"{replacement.Name}_ReplaceStandin",
            targetReturnType,
            standinParameterTypes,
            replacement.Module,
            skipVisibility: true
        );
        // The standin's own body is never executed -- ReversePatcher discards it and detours it to
        // a freshly built method carrying `replacement`'s copied IL instead -- but a DynamicMethod
        // needs some valid body before it can be handed to Harmony at all.
        standin.GetILGenerator().ThrowException(typeof(NotImplementedException));

        try
        {
            var core = harmony
                .CreateReversePatcher(replacement, new HarmonyMethod(standin))
                .Patch();
            return (core, null);
        }
        catch (Exception ex)
        {
            return (
                null,
                $"Failed to adapt the replacement method into a static call: {ex.Message}"
            );
        }
    }

    // The actual destructive prefix Harmony patches in: matches the target's real parameters by
    // Harmony's positional __0/__1/... convention rather than by name, since the replacement
    // method's own parameter names have no guaranteed relationship to the target's, sets __result
    // to `core`'s return value when the target isn't void, and always returns false so the
    // target's own body never runs.
    private static DynamicMethod BuildPrefix(
        MethodBase target,
        MethodInfo core,
        ParameterInfo[] targetParameters,
        Type targetReturnType
    )
    {
        var hasInstance = !target.IsStatic;
        var hasResult = targetReturnType != typeof(void);

        var parameterTypes = new List<Type>();
        var parameterNames = new List<string>();
        if (hasInstance)
        {
            parameterTypes.Add(typeof(object));
            parameterNames.Add(MethodPatcherTools.INSTANCE_PARAM);
        }
        for (var i = 0; i < targetParameters.Length; i++)
        {
            parameterTypes.Add(targetParameters[i].ParameterType);
            parameterNames.Add(MethodPatcherTools.PARAM_INDEX_PREFIX + i);
        }
        if (hasResult)
        {
            parameterTypes.Add(targetReturnType.MakeByRefType());
            parameterNames.Add(MethodPatcherTools.RESULT_VAR);
        }

        var shim = new DynamicMethod(
            $"{core.Name}_ReplacePrefix",
            typeof(bool),
            [.. parameterTypes],
            core.Module,
            skipVisibility: true
        );
        for (var i = 0; i < parameterNames.Count; i++)
        {
            _ = shim.DefineParameter(i + 1, ParameterAttributes.None, parameterNames[i]);
        }

        var il = shim.GetILGenerator();
        var argIndex = 0;
        if (hasInstance)
        {
            il.Emit(OpCodes.Ldarg, argIndex++);
        }
        for (var i = 0; i < targetParameters.Length; i++)
        {
            il.Emit(OpCodes.Ldarg, argIndex++);
        }
        il.Emit(OpCodes.Call, core);
        if (hasResult)
        {
            // Stind/Stobj expect [pointer, value] on the stack, but the call above already left
            // the return value on top -- stash it in a local so __result (argIndex, appended right
            // after the real parameters) can be pushed first.
            var resultLocal = il.DeclareLocal(targetReturnType);
            il.Emit(OpCodes.Stloc, resultLocal);
            il.Emit(OpCodes.Ldarg, argIndex);
            il.Emit(OpCodes.Ldloc, resultLocal);
            if (targetReturnType.IsValueType)
            {
                il.Emit(OpCodes.Stobj, targetReturnType);
            }
            else
            {
                il.Emit(OpCodes.Stind_Ref);
            }
        }
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);

        return shim;
    }
}
