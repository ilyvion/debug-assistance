namespace DebugAssistance.HotPatch;

// Owns the actual Harmony apply/remove mechanics for on-the-fly patches: one dedicated Harmony
// instance, never PatchAll'd, plus the in-memory list of currently active OnTheFlyPatches. Every
// patch this manager applies is removed individually via Harmony's own Unpatch(MethodBase,
// MethodInfo) -- never UnpatchAll -- so removing one on-the-fly patch, or reloading the assembly
// it came from, never disturbs another mod's patches on the same method, nor a second independent
// on-the-fly patch on that same method.
internal sealed class HotPatchManager
{
    internal const string HarmonyId = "ilyvion.debugassistance.hotpatch";

    private readonly object _lock = new();
    private readonly Harmony _harmony;
    private readonly List<OnTheFlyPatch> _activePatches = [];

    internal HotPatchManager()
        : this(HarmonyId) { }

    internal HotPatchManager(string harmonyId)
    {
        _harmony = new Harmony(harmonyId);
    }

    internal IReadOnlyList<OnTheFlyPatch> ActivePatches
    {
        get
        {
            lock (_lock)
            {
                return [.. _activePatches];
            }
        }
    }

    // Harmony throws at patch time for signature mismatches (wrong prefix/postfix return type,
    // missing __instance/__result/__state conventions, wrong transpiler/finalizer shape) -- caught
    // here and returned as a message rather than left to propagate, instead of crashing whatever
    // called Apply. Also logged via Log.Error so the failure shows up in our own Error
    // Inspector (LogCaptureHook watches Unity's log stream) instead of only ever being visible as
    // this one HTTP response.
    internal (OnTheFlyPatch? Patch, string? Error) Apply(
        MethodBase target,
        MethodInfo patchMethod,
        OnTheFlyPatchType patchType,
        string sourceAssemblyPath,
        int sourceAssemblyGeneration
    )
    {
        // Harmony's manual Patch(target, prefix: new HarmonyMethod(patchMethod)) API has no way to
        // supply an instance for a non-static patch method -- it patches it in as a direct call
        // site regardless. MonoMod's IL merge then emits a call Harmony's own JIT can't resolve,
        // surfacing as an opaque "Invalid IL code ... call 0x00000001"-style error instead of the
        // real problem. Rejecting it here up front, before Harmony ever sees it, gives a much more
        // actionable message for what is otherwise the single most common way to trip that error.
        // Replace is exempt: ReplacePatchBuilder always produces a static shim regardless of
        // whether the player's replacement method itself is static.
        if (!patchMethod.IsStatic && patchType != OnTheFlyPatchType.Replace)
        {
            return (
                null,
                $"Patch methods must be static; \"{CSharpTypeFormatter.DescribeMethod(patchMethod)}\" is an instance method."
            );
        }

        MethodInfo appliedMethod;
        if (patchType == OnTheFlyPatchType.Replace)
        {
            var (shim, buildError) = ReplacePatchBuilder.Build(_harmony, target, patchMethod);
            if (shim is null)
            {
                return (null, buildError);
            }
            appliedMethod = shim;
        }
        else
        {
            appliedMethod = patchMethod;
        }

        try
        {
            var method = new HarmonyMethod(appliedMethod);
            _ = patchType switch
            {
                OnTheFlyPatchType.Prefix or OnTheFlyPatchType.Replace => _harmony.Patch(
                    target,
                    prefix: method
                ),
                OnTheFlyPatchType.Postfix => _harmony.Patch(target, postfix: method),
                OnTheFlyPatchType.Transpiler => _harmony.Patch(target, transpiler: method),
                OnTheFlyPatchType.Finalizer => _harmony.Patch(target, finalizer: method),
                _ => throw new ArgumentOutOfRangeException(nameof(patchType), patchType, null),
            };
        }
        catch (Exception ex)
        {
            Log.Error($"[DebugAssistance] Applying an on-the-fly {patchType} patch failed: {ex}");
            return (null, DescribeApplyFailure(ex));
        }

        var patch = OnTheFlyPatch.Create(
            target,
            patchMethod,
            appliedMethod,
            patchType,
            sourceAssemblyPath,
            sourceAssemblyGeneration
        );
        lock (_lock)
        {
            _activePatches.Add(patch);
        }
        return (patch, null);
    }

    // HarmonyException (thrown when MonoMod's generated replacement method fails IL
    // verification -- e.g. an unresolvable call token) carries the actual generated instruction
    // list plus the index of the one that failed, which is far more actionable than its own flat
    // .Message (just the raw JIT error text). Anything else falls back to ex.Message directly.
    private static string DescribeApplyFailure(Exception ex) =>
        ex is HarmonyException harmonyException
        && harmonyException.GetErrorIndex() is var index
        && index >= 0
            ? FormatIlDiagnostic(
                harmonyException.InnerException?.Message ?? harmonyException.Message,
                harmonyException.GetInstructions(),
                index
            )
            : ex.Message;

    // Split out from DescribeApplyFailure so it's directly unit-testable: HarmonyException's own
    // constructors are internal to HarmonyLib, so a genuine instance can't be built from a test.
    internal static string FormatIlDiagnostic(
        string headline,
        IReadOnlyList<CodeInstruction> instructions,
        int errorIndex
    )
    {
        var start = Math.Max(0, errorIndex - 3);
        var end = Math.Min(instructions.Count - 1, errorIndex + 3);
        var contextLines = Enumerable
            .Range(start, end - start + 1)
            .Select(i => (i == errorIndex ? "-> " : "   ") + instructions[i]);
        return string.Join(
            Environment.NewLine,
            [headline, "", "Generated IL around the failing instruction:", .. contextLines]
        );
    }

    // Surgical per-MethodInfo removal -- never UnpatchAll -- so this never disturbs another mod's
    // patches on the same target, nor a second independent on-the-fly patch on it. Returns false
    // without touching Harmony at all if `patch` isn't (or is no longer) active.
    internal bool Remove(OnTheFlyPatch patch)
    {
        lock (_lock)
        {
            if (!_activePatches.Remove(patch))
            {
                return false;
            }
        }

        _harmony.Unpatch(patch.Target, patch.AppliedMethod);
        if (patch.PatchType == OnTheFlyPatchType.Replace)
        {
            ReplacePatchBuilder.Forget(patch.Target);
        }
        return true;
    }

    // Every active patch whose patch method belongs to `assembly`, without touching Harmony or
    // removing anything. Used to ask the player for confirmation before a reload would discard
    // these; the player's chosen subset is then removed via RemoveMany by id.
    internal IReadOnlyList<OnTheFlyPatch> PatchesFromAssembly(Assembly assembly)
    {
        lock (_lock)
        {
            return
            [
                .. _activePatches.Where(patch => patch.PatchMethod.Module.Assembly == assembly),
            ];
        }
    }

    // Removes every currently active patch whose id is in `ids`, skipping any id that doesn't
    // match an active patch -- the multi-select "Remove selected" action in the active-patches
    // list. Same surgical per-patch Unpatch as Remove above, just looped.
    internal IReadOnlyList<OnTheFlyPatch> RemoveMany(HashSet<Guid> ids)
    {
        List<OnTheFlyPatch> removed;
        lock (_lock)
        {
            removed = [.. _activePatches.Where(patch => ids.Contains(patch.Id))];
            foreach (var patch in removed)
            {
                _ = _activePatches.Remove(patch);
            }
        }

        foreach (var patch in removed)
        {
            _harmony.Unpatch(patch.Target, patch.AppliedMethod);
            if (patch.PatchType == OnTheFlyPatchType.Replace)
            {
                ReplacePatchBuilder.Forget(patch.Target);
            }
        }

        return removed;
    }
}
