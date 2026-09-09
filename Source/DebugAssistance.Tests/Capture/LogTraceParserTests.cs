using DebugAssistance.Capture;
using RimTestRedux;

namespace DebugAssistance.Tests.Capture;

[TestSuite]
internal static class LogTraceParserTests
{
    [Test]
    public static void ParseExtractsErrorTypeAndMessageFromAContextuallyWrappedHeader()
    {
        var logText =
            "Exception ticking Pawn Bob: System.NullReferenceException: "
            + "Object reference not set to an instance of an object\n"
            + "  at Foo.Bar () [0x00000] in <b4d967f0d45a413fbb0223eefee4f2ae>:0";

        var parsed = LogTraceParser.Parse(logText);

        Assert.That(parsed.ErrorTypeName).Is.EqualTo("System.NullReferenceException");
        Assert
            .That(parsed.Message)
            .Is.EqualTo("Object reference not set to an instance of an object");
    }

    // A shipped-PDB file:line, still just display metadata.
    [Test]
    public static void ParseExtractsFileAndLineWhenAShippedPdbLocationIsPresent()
    {
        var logText =
            "System.Exception: boom\n"
            + "  at foo.Patch_PawnGenerator_GenerateNewPawnInternal.Postfix "
            + "(RimWorld.Pawn pawn) [0x00000] in /foo/Harmony/Patch_PawnGenerator.cs:33";

        var parsed = LogTraceParser.Parse(logText);

        Assert.ThatCollection(parsed.Frames).Has.Count(1);
        var frame = parsed.Frames[0];
        Assert
            .That(frame.DeclaringTypeName)
            .Is.EqualTo("foo.Patch_PawnGenerator_GenerateNewPawnInternal");
        Assert.That(frame.MethodName).Is.EqualTo("Postfix");
        Assert.That(frame.FileName).Is.EqualTo("/foo/Harmony/Patch_PawnGenerator.cs");
        Assert.That(frame.LineNumber!.Value).Is.EqualTo(33);
    }

    // Method + IL offset only, no usable file:line — the module-GUID form must not be mistaken
    // for a real path.
    [Test]
    public static void ParseLeavesFileAndLineNullWhenOnlyAModuleGuidLocationIsPresent()
    {
        var logText =
            "System.Exception: boom\n"
            + "  at RimWorld.PawnGroupKindWorker_Normal.GeneratePawns "
            + "(RimWorld.PawnGroupMakerParms parms) [0x002d1] in <b4d967f0d45a413fbb0223eefee4f2ae>:0";

        var parsed = LogTraceParser.Parse(logText);

        Assert.ThatCollection(parsed.Frames).Has.Count(1);
        var frame = parsed.Frames[0];
        Assert.That(frame.DeclaringTypeName).Is.EqualTo("RimWorld.PawnGroupKindWorker_Normal");
        Assert.That(frame.MethodName).Is.EqualTo("GeneratePawns");
        Assert.That(frame.FileName is null).Is.True();
        Assert.That(frame.LineNumber is null).Is.True();
    }

    // Plain .NET-style location text ("in file:line 45"), as seen when HarmonyMod's
    // noStacktraceEnhancing setting is on.
    [Test]
    public static void ParseHandlesPlainClrStyleLocationsWithALineKeyword()
    {
        var logText =
            "System.InvalidOperationException: Collection was modified\n"
            + "   at Verse.TickList.Tick() in C:\\Verse\\TickList.cs:line 45";

        var parsed = LogTraceParser.Parse(logText);

        Assert.ThatCollection(parsed.Frames).Has.Count(1);
        var frame = parsed.Frames[0];
        Assert.That(frame.DeclaringTypeName).Is.EqualTo("Verse.TickList");
        Assert.That(frame.MethodName).Is.EqualTo("Tick");
        Assert.That(frame.FileName).Is.EqualTo("C:\\Verse\\TickList.cs");
        Assert.That(frame.LineNumber!.Value).Is.EqualTo(45);
    }

    // The inspector falls back to displaying this when a frame has no file:line info at all.
    [Test]
    public static void ParseExtractsTheIlOffsetFromTheBracketedHexValue()
    {
        var logText =
            "System.Exception: boom\n"
            + "  at RimWorld.PawnGroupKindWorker_Normal.GeneratePawns "
            + "(RimWorld.PawnGroupMakerParms parms) [0x002d1] in <b4d967f0d45a413fbb0223eefee4f2ae>:0";

        var parsed = LogTraceParser.Parse(logText);

        Assert.ThatCollection(parsed.Frames).Has.Count(1);
        Assert.That(parsed.Frames[0].IlOffset!.Value).Is.EqualTo(0x2d1);
    }

    [Test]
    public static void ParseHandlesMultipleFrameLinesInOrder()
    {
        var logText =
            "System.Exception: boom\n"
            + "  at Foo.Inner () [0x00000] in <guid>:0\n"
            + "  at Foo.Outer () [0x00010] in <guid>:0";

        var parsed = LogTraceParser.Parse(logText);

        Assert.ThatCollection(parsed.Frames).Has.Count(2);
        Assert.That(parsed.Frames[0].MethodName).Is.EqualTo("Inner");
        Assert.That(parsed.Frames[1].MethodName).Is.EqualTo("Outer");
    }

    [Test]
    public static void ParseDegradesGracefullyWhenNoRecognizableHeaderOrFramesArePresent()
    {
        var logText = "[Ref 1A2B3C4D] Duplicate stacktrace, see ref for original";

        var parsed = LogTraceParser.Parse(logText);

        Assert.That(parsed.ErrorTypeName).Is.EqualTo("");
        Assert.ThatCollection(parsed.Frames).Is.Empty();
    }

    [Test]
    public static void ParseHarmonyRefHashExtractsTheHexHashFromAnEnhancedFirstSighting()
    {
        var logText =
            "[Ref 1A2B3C4D]\nSystem.NullReferenceException: boom\n"
            + "  at Foo.Bar () [0x00000] in <guid>:0";

        var hash = LogTraceParser.ParseHarmonyRefHash(logText);

        Assert.That(hash!.Value).Is.EqualTo(0x1A2B3C4D);
    }

    [Test]
    public static void ParseHarmonyRefHashExtractsTheHexHashFromACollapsedDuplicate()
    {
        var hash = LogTraceParser.ParseHarmonyRefHash(
            "[Ref 1A2B3C4D] Duplicate stacktrace, see ref for original"
        );

        Assert.That(hash!.Value).Is.EqualTo(0x1A2B3C4D);
    }

    [Test]
    public static void ParseHarmonyRefHashReturnsNullWhenNoRefPrefixIsPresent()
    {
        var hash = LogTraceParser.ParseHarmonyRefHash("System.Exception: boom");

        Assert.That(hash is null).Is.True();
    }

    [Test]
    public static void ParseHarmonyRefHashDoesNotThrowWhenTheHexRunExceedsIntWidth()
    {
        var hash = LogTraceParser.ParseHarmonyRefHash("[Ref 1A2B3C4D5E] boom");

        Assert.That(hash is null).Is.True();
    }

    [Test]
    public static void IsCollapsedDuplicateDetectsHarmonyModsPlaceholderText() =>
        Assert
            .That(
                LogTraceParser.IsCollapsedDuplicate(
                    "[Ref 1A2B3C4D] Duplicate stacktrace, see ref for original"
                )
            )
            .Is.True();

    [Test]
    public static void IsCollapsedDuplicateIsFalseForARegularStackTrace() =>
        Assert
            .That(
                LogTraceParser.IsCollapsedDuplicate(
                    "System.Exception: boom\n  at Foo.Bar () [0x00000] in <guid>:0"
                )
            )
            .Is.False();
}
