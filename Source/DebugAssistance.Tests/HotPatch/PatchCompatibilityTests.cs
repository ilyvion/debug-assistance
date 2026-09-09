using System.Reflection.Emit;
using DebugAssistance.HotPatch;
using RimTestRedux;

namespace DebugAssistance.Tests.HotPatch;

[TestSuite]
internal static class PatchCompatibilityTests
{
    private sealed class TargetFixture
    {
        public static string SomeField = "value";

        public static bool StaticTarget(int index, ref bool flag) => flag && index == 0;

        public static void VoidTarget() { }

#pragma warning disable CA1822 // Mark members as static -- deliberately an instance method
        public bool InstanceTarget(string label) => label.Length == 0;
#pragma warning restore CA1822
    }

    // Every member here stands in for a candidate patch method resolved by name via Candidate()
    // below -- none is ever actually invoked, only reflected over, so their parameters are
    // deliberately unread.
#pragma warning disable IDE0060 // Remove unused parameter
    private sealed class PatchFixtureMethods
    {
        public static void VoidPrefix() { }

        public static bool BoolPrefix() => true;

        public static int IntPrefix() => 0;

        public static void VoidPostfix() { }

        public static bool MatchingReturnPostfix() => true;

        public static string MismatchedReturnPostfix() => "";

        public static void ResultRefPrefix(ref bool __result) { }

        public static void InstanceParamPrefix(TargetFixture __instance) { }

        public static void FieldAccessPrefix(ref string ___SomeField) { }

        public static void UnknownFieldAccessPrefix(ref string ___NoSuchField) { }

        public static void NamedParamPrefix(int index) { }

        public static void PositionalParamPrefix(int __0) { }

        public static void UnknownNamedParamPrefix(int notAParam) { }

        public static void StateParamPrefix(ref object __state) { }

        public static void ExceptionParamFinalizer(Exception __exception) { }

        public static Exception ExceptionReturnFinalizer() => null!;

        public static int InvalidFinalizerReturn() => 0;

        public static IEnumerable<CodeInstruction> ValidTranspiler(
            IEnumerable<CodeInstruction> instructions
        ) => instructions;

        public static IEnumerable<CodeInstruction> ValidTranspilerWithExtras(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator il,
            MethodBase original
        ) => instructions;

        public static void InvalidTranspilerReturnType(
            IEnumerable<CodeInstruction> instructions
        ) { }

        public static IEnumerable<CodeInstruction> InvalidTranspilerParam(
            IEnumerable<CodeInstruction> instructions,
            int extra
        ) => instructions;

#pragma warning disable CA1822 // Mark members as static -- deliberately an instance method
        public bool NotStatic() => true;
#pragma warning restore CA1822
    }
#pragma warning restore IDE0060

    private static MethodInfo Target(string name) => typeof(TargetFixture).GetMethod(name);

    private static MethodInfo Candidate(string name) => typeof(PatchFixtureMethods).GetMethod(name);

    [Test]
    public static void RejectsANonStaticCandidateRegardlessOfPatchType() =>
        Assert
            .That(
                PatchCompatibility.IsCompatible(
                    Target(nameof(TargetFixture.StaticTarget)),
                    Candidate(nameof(PatchFixtureMethods.NotStatic)),
                    HarmonyPatchType.Prefix
                )
            )
            .Is.False();

    [Test]
    public static void PrefixAcceptsVoidAndBoolReturnTypes()
    {
        var target = Target(nameof(TargetFixture.StaticTarget));

        Assert
            .That(
                PatchCompatibility.IsCompatible(
                    target,
                    Candidate(nameof(PatchFixtureMethods.VoidPrefix)),
                    HarmonyPatchType.Prefix
                )
            )
            .Is.True();
        Assert
            .That(
                PatchCompatibility.IsCompatible(
                    target,
                    Candidate(nameof(PatchFixtureMethods.BoolPrefix)),
                    HarmonyPatchType.Prefix
                )
            )
            .Is.True();
    }

    [Test]
    public static void PrefixRejectsANonBoolNonVoidReturnType() =>
        Assert
            .That(
                PatchCompatibility.IsCompatible(
                    Target(nameof(TargetFixture.StaticTarget)),
                    Candidate(nameof(PatchFixtureMethods.IntPrefix)),
                    HarmonyPatchType.Prefix
                )
            )
            .Is.False();

    [Test]
    public static void PostfixAcceptsVoidOrAReturnTypeMatchingTheTargets()
    {
        var target = Target(nameof(TargetFixture.StaticTarget));

        Assert
            .That(
                PatchCompatibility.IsCompatible(
                    target,
                    Candidate(nameof(PatchFixtureMethods.VoidPostfix)),
                    HarmonyPatchType.Postfix
                )
            )
            .Is.True();
        Assert
            .That(
                PatchCompatibility.IsCompatible(
                    target,
                    Candidate(nameof(PatchFixtureMethods.MatchingReturnPostfix)),
                    HarmonyPatchType.Postfix
                )
            )
            .Is.True();
    }

    [Test]
    public static void PostfixRejectsAReturnTypeThatDoesNotMatchTheTargets() =>
        Assert
            .That(
                PatchCompatibility.IsCompatible(
                    Target(nameof(TargetFixture.StaticTarget)),
                    Candidate(nameof(PatchFixtureMethods.MismatchedReturnPostfix)),
                    HarmonyPatchType.Postfix
                )
            )
            .Is.False();

    [Test]
    public static void PostfixRejectsANonVoidReturnTypeWhenTheTargetItselfReturnsVoid() =>
        Assert
            .That(
                PatchCompatibility.IsCompatible(
                    Target(nameof(TargetFixture.VoidTarget)),
                    Candidate(nameof(PatchFixtureMethods.MatchingReturnPostfix)),
                    HarmonyPatchType.Postfix
                )
            )
            .Is.False();

    [Test]
    public static void ResultParameterIsAcceptedOnlyWhenTheTargetReturnsANonVoidType()
    {
        Assert
            .That(
                PatchCompatibility.IsCompatible(
                    Target(nameof(TargetFixture.StaticTarget)),
                    Candidate(nameof(PatchFixtureMethods.ResultRefPrefix)),
                    HarmonyPatchType.Prefix
                )
            )
            .Is.True();
        Assert
            .That(
                PatchCompatibility.IsCompatible(
                    Target(nameof(TargetFixture.VoidTarget)),
                    Candidate(nameof(PatchFixtureMethods.ResultRefPrefix)),
                    HarmonyPatchType.Prefix
                )
            )
            .Is.False();
    }

    [Test]
    public static void InstanceParameterIsAcceptedOnlyAgainstAnInstanceTarget()
    {
        Assert
            .That(
                PatchCompatibility.IsCompatible(
                    Target(nameof(TargetFixture.InstanceTarget)),
                    Candidate(nameof(PatchFixtureMethods.InstanceParamPrefix)),
                    HarmonyPatchType.Prefix
                )
            )
            .Is.True();
        Assert
            .That(
                PatchCompatibility.IsCompatible(
                    Target(nameof(TargetFixture.StaticTarget)),
                    Candidate(nameof(PatchFixtureMethods.InstanceParamPrefix)),
                    HarmonyPatchType.Prefix
                )
            )
            .Is.False();
    }

    [Test]
    public static void FieldAccessParameterIsAcceptedWhenTheNamedFieldExistsOnTheTargetsDeclaringType() =>
        Assert
            .That(
                PatchCompatibility.IsCompatible(
                    Target(nameof(TargetFixture.StaticTarget)),
                    Candidate(nameof(PatchFixtureMethods.FieldAccessPrefix)),
                    HarmonyPatchType.Prefix
                )
            )
            .Is.True();

    [Test]
    public static void FieldAccessParameterIsRejectedWhenNoSuchFieldExists() =>
        Assert
            .That(
                PatchCompatibility.IsCompatible(
                    Target(nameof(TargetFixture.StaticTarget)),
                    Candidate(nameof(PatchFixtureMethods.UnknownFieldAccessPrefix)),
                    HarmonyPatchType.Prefix
                )
            )
            .Is.False();

    [Test]
    public static void ParameterNamedAfterAnOriginalParameterIsAccepted() =>
        Assert
            .That(
                PatchCompatibility.IsCompatible(
                    Target(nameof(TargetFixture.StaticTarget)),
                    Candidate(nameof(PatchFixtureMethods.NamedParamPrefix)),
                    HarmonyPatchType.Prefix
                )
            )
            .Is.True();

    [Test]
    public static void ParameterNamedByPositionalIndexIsAccepted() =>
        Assert
            .That(
                PatchCompatibility.IsCompatible(
                    Target(nameof(TargetFixture.StaticTarget)),
                    Candidate(nameof(PatchFixtureMethods.PositionalParamPrefix)),
                    HarmonyPatchType.Prefix
                )
            )
            .Is.True();

    [Test]
    public static void ParameterMatchingNoOriginalParameterIsRejected() =>
        Assert
            .That(
                PatchCompatibility.IsCompatible(
                    Target(nameof(TargetFixture.StaticTarget)),
                    Candidate(nameof(PatchFixtureMethods.UnknownNamedParamPrefix)),
                    HarmonyPatchType.Prefix
                )
            )
            .Is.False();

    [Test]
    public static void StateParameterIsAlwaysAccepted() =>
        Assert
            .That(
                PatchCompatibility.IsCompatible(
                    Target(nameof(TargetFixture.VoidTarget)),
                    Candidate(nameof(PatchFixtureMethods.StateParamPrefix)),
                    HarmonyPatchType.Prefix
                )
            )
            .Is.True();

    [Test]
    public static void FinalizerAcceptsAnExceptionParameterAndAnExceptionOrVoidReturnType()
    {
        var target = Target(nameof(TargetFixture.StaticTarget));

        Assert
            .That(
                PatchCompatibility.IsCompatible(
                    target,
                    Candidate(nameof(PatchFixtureMethods.ExceptionParamFinalizer)),
                    HarmonyPatchType.Finalizer
                )
            )
            .Is.True();
        Assert
            .That(
                PatchCompatibility.IsCompatible(
                    target,
                    Candidate(nameof(PatchFixtureMethods.ExceptionReturnFinalizer)),
                    HarmonyPatchType.Finalizer
                )
            )
            .Is.True();
    }

    [Test]
    public static void FinalizerRejectsAReturnTypeThatIsNeitherVoidNorAnException() =>
        Assert
            .That(
                PatchCompatibility.IsCompatible(
                    Target(nameof(TargetFixture.StaticTarget)),
                    Candidate(nameof(PatchFixtureMethods.InvalidFinalizerReturn)),
                    HarmonyPatchType.Finalizer
                )
            )
            .Is.False();

    [Test]
    public static void TranspilerAcceptsTheStandardCodeInstructionShapeWithOptionalExtras()
    {
        var target = Target(nameof(TargetFixture.StaticTarget));

        Assert
            .That(
                PatchCompatibility.IsCompatible(
                    target,
                    Candidate(nameof(PatchFixtureMethods.ValidTranspiler)),
                    HarmonyPatchType.Transpiler
                )
            )
            .Is.True();
        Assert
            .That(
                PatchCompatibility.IsCompatible(
                    target,
                    Candidate(nameof(PatchFixtureMethods.ValidTranspilerWithExtras)),
                    HarmonyPatchType.Transpiler
                )
            )
            .Is.True();
    }

    [Test]
    public static void TranspilerRejectsAWrongReturnTypeOrAnUnexpectedParameterType()
    {
        var target = Target(nameof(TargetFixture.StaticTarget));

        Assert
            .That(
                PatchCompatibility.IsCompatible(
                    target,
                    Candidate(nameof(PatchFixtureMethods.InvalidTranspilerReturnType)),
                    HarmonyPatchType.Transpiler
                )
            )
            .Is.False();
        Assert
            .That(
                PatchCompatibility.IsCompatible(
                    target,
                    Candidate(nameof(PatchFixtureMethods.InvalidTranspilerParam)),
                    HarmonyPatchType.Transpiler
                )
            )
            .Is.False();
    }
}
