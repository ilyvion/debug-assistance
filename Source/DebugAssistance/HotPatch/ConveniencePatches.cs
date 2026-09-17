namespace DebugAssistance.HotPatch;

// DebugAssistance's own built-in convenience patches -- ready-made Harmony patch bodies for the
// most common ad-hoc debugging needs, offered by the hot-patch panel's convenience-patch picker
// for any target method the player selects. Each one follows the same [ConveniencePatch] contract
// any other mod or hot-patch assembly can also implement (see ConveniencePatchAttribute): only
// Harmony's special parameter names, never a signature matched to a particular target.
internal static class ConveniencePatches
{
#pragma warning disable IDE0051 // Used by reflection/Harmony, not by any direct call

    [ConveniencePatch(
        "Skip method entirely",
        "Prefix that returns false, so the original method's body never runs.",
        ConveniencePatchType.Prefix
    )]
    private static bool SkipMethod() => false;

    [ConveniencePatch(
        "Catch and log exception",
        "Finalizer that logs any exception thrown by the method and swallows it.",
        ConveniencePatchType.Finalizer
    )]
    private static Exception? CatchAndLogException(
        MethodBase __originalMethod,
        Exception __exception
    )
    {
        if (__exception is not null)
        {
            Log.Error(
                $"[DebugAssistance(CatchAndLogException)] {CSharpTypeFormatter.DescribeMethod(__originalMethod)} threw: {__exception}"
            );
        }
        return null;
    }

    [ConveniencePatch(
        "Print all arguments",
        "Prefix that debug-logs every argument passed to the method.",
        ConveniencePatchType.Prefix
    )]
    private static void PrintArguments(MethodBase __originalMethod, object[] __args) =>
        Log.Message(
            $"[DebugAssistance(PrintArguments)] {CSharpTypeFormatter.DescribeMethod(__originalMethod)} called with: {string.Join(", ", __args.Select(FormatValue))}"
        );

    [ConveniencePatch(
        "Print return value",
        "Postfix that debug-logs the method's return value.",
        ConveniencePatchType.Postfix
    )]
    private static void PrintReturnValue(MethodBase __originalMethod, object __result) =>
        Log.Message(
            $"[DebugAssistance(PrintReturnValue)] {CSharpTypeFormatter.DescribeMethod(__originalMethod)} returned: {FormatValue(__result)}"
        );

    [ConveniencePatch(
        "Log method call",
        "Prefix that debug-logs that the method was called, with no other effect.",
        ConveniencePatchType.Prefix
    )]
    private static void LogMethodCall(MethodBase __originalMethod) =>
        Log.Message(
            $"[DebugAssistance(LogMethodCall)] {CSharpTypeFormatter.DescribeMethod(__originalMethod)} was called."
        );

    [ConveniencePatch(
        "Log call with stack trace",
        "Prefix that debug-logs the method call together with the call stack that reached it.",
        ConveniencePatchType.Prefix
    )]
    private static void LogCallWithStackTrace(MethodBase __originalMethod) =>
        Log.Message(
            $"[DebugAssistance(LogCallWithStackTrace)] {CSharpTypeFormatter.DescribeMethod(__originalMethod)} was called from:\n{Environment.StackTrace}"
        );

    [ConveniencePatch(
        "Force null return value",
        "Postfix that overwrites the method's return value with null. Only for methods returning a reference type.",
        ConveniencePatchType.Postfix
    )]
    private static void ForceNullReturnValue(ref object __result) => __result = null!;

#pragma warning restore IDE0051

    private static string FormatValue(object? value) => value?.ToString() ?? "null";
}
