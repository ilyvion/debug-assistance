namespace DebugAssistance.HotPatch;

/// <summary>
/// The role a <see cref="ConveniencePatchAttribute"/>-attributed method can fill.
/// </summary>
/// <remarks>
/// Only the three roles a convenience patch can meaningfully fill without knowing its eventual
/// target's signature ahead of time -- Transpiler needs IL specific to one method, and Replace
/// needs a parameter/return-type match, so neither fits a body meant to be reused as-is against
/// whatever method the player picks. A dedicated public enum rather than reusing the internal
/// OnTheFlyPatchType directly, since that type's other members (and its own doc comments) are an
/// implementation detail this public attribute shouldn't expose to third parties.
/// </remarks>
public enum ConveniencePatchType
{
    /// <summary>Runs before the target method.</summary>
    Prefix,

    /// <summary>Runs after the target method.</summary>
    Postfix,

    /// <summary>Runs after the target method, and after any Postfix, even if it threw.</summary>
    Finalizer,
}

/// <summary>
/// Marks a static method as a ready-made Harmony patch body that DebugAssistance's hot-patch panel
/// offers as a one-click Prefix/Postfix/Finalizer for any target method the player selects,
/// instead of requiring a custom patch to be written by hand each time.
/// </summary>
/// <remarks>
/// Apply it to a method in DebugAssistance itself (see ConveniencePatches.cs), in any other loaded
/// mod's assembly, or in a project scaffolded from the hot-patch panel (which references
/// DebugAssistance.dll for exactly this purpose) -- ConveniencePatchScanner finds it in any of
/// those places the same way, both at startup and on the panel's "Rescan" button.
///
/// The attributed method follows ordinary Harmony special-parameter-name conventions
/// (__originalMethod, __args, __result, __exception, ...) rather than matching any particular
/// target's real parameter names/types, since it's applied as-is against whatever method the
/// player picks, with no per-target signature generation involved.
/// </remarks>
/// <param name="name">A short, human-readable name shown in the hot-patch panel's picker.</param>
/// <param name="description">A one- or two-sentence explanation of what the patch does.</param>
/// <param name="patchType">Which Harmony patch role the attributed method fills.</param>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ConveniencePatchAttribute(
    string name,
    string description,
    ConveniencePatchType patchType
) : Attribute
{
    /// <summary>A short, human-readable name shown in the hot-patch panel's picker.</summary>
    public string Name { get; } = name;

    /// <summary>A one- or two-sentence explanation of what the patch does.</summary>
    public string Description { get; } = description;

    /// <summary>Which Harmony patch role the attributed method fills.</summary>
    public ConveniencePatchType PatchType { get; } = patchType;
}
