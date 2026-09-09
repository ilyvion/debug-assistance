using DebugAssistance.Capture;
using RimTestRedux;

namespace DebugAssistance.Tests.Capture;

// Round-trips CapturedError/CapturedStackFrame's IExposable implementation through a real
// Scribe save/load cycle, using ilyvion.Laboratory's CustomStream Scribe helpers so no on-disk
// save file is needed (see CustomStreamScribeSaver/CustomStreamReaderScribeLoader) — actual file
// I/O (CaptureFileIO, GetSavedFilesList, the mod-mismatch confirmation dialog) stays a manual
// verification item.
[TestSuite]
internal static class CapturedErrorExposeDataTests
{
    // Scribe's save/load lifecycle closes whatever stream it was handed; wrapping it makes the
    // underlying MemoryStream survive so it can still be read back afterwards.
    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) =>
            inner.Write(buffer, offset, count);
    }

    // Exercises the same ExposeData wiring real save/load uses
    // (Scribe_Collections.Look(ref list, "exceptions", LookMode.Deep)), just against an
    // in-memory stream instead of a file path.
    private sealed class CaptureListHarness : IExposable
    {
        public List<CapturedError> Errors = [];

        public void ExposeData() =>
            Scribe_Collections.Look(ref Errors, "exceptions", LookMode.Deep);
    }

    private static void RoundTrip<T>(T saveTarget, T loadTarget)
        where T : IExposable
    {
        using var memory = new MemoryStream();
        using (var nonClosing = new NonClosingStream(memory))
        {
            CustomStreamScribeSaver.InitSaving(nonClosing, "root");
            try
            {
                saveTarget.ExposeData();
                Scribe.saver.FinalizeSaving();
            }
            finally
            {
                if (Scribe.mode != LoadSaveMode.Inactive)
                {
                    Scribe.ForceStop();
                }
            }
        }

        memory.Position = 0;
        using (var reader = new StreamReader(memory))
        {
            CustomStreamReaderScribeLoader.InitLoading(reader);
        }
        try
        {
            Scribe.loader.curParent = loadTarget;
            loadTarget.ExposeData();
            Scribe.loader.FinalizeLoading();
        }
        finally
        {
            if (Scribe.mode != LoadSaveMode.Inactive)
            {
                Scribe.ForceStop();
            }
        }
    }

    [Test]
    public static void SaveThenLoadRoundTripsAllExposedFields()
    {
        var frame = new CapturedStackFrame(
            "at Foo.Bar() in Foo.cs:12",
            "Foo",
            "Bar",
            "Foo.cs",
            12,
            columnNumber: 5,
            ilOffset: 0x1F
        )
        {
            ResolvedModName = "Some Mod",
            ResolvedAssemblyShortName = "SomeMod.dll",
        };
        frame.SetPatches([
            new CapturedPatchFrame("some.mod.id", "prefix", typeof(object).GetMethod("ToString")!),
        ]);
        var original = new CapturedError(
            "System.NullReferenceException",
            "Object reference not set",
            "at Foo.Bar() in Foo.cs:12",
            [frame],
            new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)
        )
        {
            HarmonyRefHash = 0xABCD,
        };
        original.RecordOccurrence(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        var saveHarness = new CaptureListHarness { Errors = [original] };
        var loadHarness = new CaptureListHarness();

        RoundTrip(saveHarness, loadHarness);

        Assert.ThatCollection(loadHarness.Errors).Has.Count(1);
        var roundTripped = loadHarness.Errors[0];
        Assert.That(roundTripped.ErrorTypeName).Is.EqualTo(original.ErrorTypeName);
        Assert.That(roundTripped.Message).Is.EqualTo(original.Message);
        Assert.That(roundTripped.RawStackTrace).Is.EqualTo(original.RawStackTrace);
        Assert.That(roundTripped.DedupeKey).Is.EqualTo(original.DedupeKey);
        Assert.That(roundTripped.FirstSeen).Is.EqualTo(original.FirstSeen);
        Assert.That(roundTripped.LastSeen).Is.EqualTo(original.LastSeen);
        Assert.That(roundTripped.OccurrenceCount).Is.EqualTo(original.OccurrenceCount);
        Assert.That(roundTripped.HarmonyRefHash).Is.EqualTo(original.HarmonyRefHash);

        Assert.ThatCollection(roundTripped.Frames).Has.Count(1);
        var roundTrippedFrame = roundTripped.Frames[0];
        Assert.That(roundTrippedFrame.RawText).Is.EqualTo(frame.RawText);
        Assert.That(roundTrippedFrame.DeclaringTypeName).Is.EqualTo(frame.DeclaringTypeName!);
        Assert.That(roundTrippedFrame.MethodName).Is.EqualTo(frame.MethodName!);
        Assert.That(roundTrippedFrame.FileName).Is.EqualTo(frame.FileName!);
        Assert.That(roundTrippedFrame.LineNumber).Is.EqualTo(frame.LineNumber!);
        Assert.That(roundTrippedFrame.ColumnNumber).Is.EqualTo(frame.ColumnNumber!);
        Assert.That(roundTrippedFrame.IlOffset).Is.EqualTo(frame.IlOffset!);
        Assert.That(roundTrippedFrame.ResolvedModName).Is.EqualTo(frame.ResolvedModName!);
        Assert
            .That(roundTrippedFrame.ResolvedAssemblyShortName)
            .Is.EqualTo(frame.ResolvedAssemblyShortName!);
        // Never Scribed — Type/MethodBase/Assembly must not be persisted directly. Must stay
        // absent after a load.
        Assert.That(roundTrippedFrame.Method is null).Is.True();
        Assert.That(roundTrippedFrame.Assembly is null).Is.True();

        Assert.ThatCollection(roundTrippedFrame.Patches).Has.Count(1);
        var roundTrippedPatch = roundTrippedFrame.Patches[0];
        var originalPatch = frame.Patches[0];
        Assert.That(roundTrippedPatch.OwnerModId).Is.EqualTo(originalPatch.OwnerModId);
        Assert.That(roundTrippedPatch.PatchKind).Is.EqualTo(originalPatch.PatchKind);
        Assert
            .That(roundTrippedPatch.DeclaringTypeName)
            .Is.EqualTo(originalPatch.DeclaringTypeName!);
        Assert.That(roundTrippedPatch.MethodName).Is.EqualTo(originalPatch.MethodName!);
        // Never Scribed, same rule as the frame's own Method/Assembly.
        Assert.That(roundTrippedPatch.Method is null).Is.True();
    }

    [Test]
    public static void SaveThenLoadRoundTripsAnEmptyErrorList()
    {
        var saveHarness = new CaptureListHarness();
        var loadHarness = new CaptureListHarness();

        RoundTrip(saveHarness, loadHarness);

        Assert.ThatCollection(loadHarness.Errors).Is.Empty();
    }

    [Test]
    public static void SaveThenLoadRoundTripsAnErrorWithNoFrames()
    {
        var original = new CapturedError(
            "System.Exception",
            "message",
            "trace",
            [],
            new DateTime(2026, 1, 1)
        );
        var saveHarness = new CaptureListHarness { Errors = [original] };
        var loadHarness = new CaptureListHarness();

        RoundTrip(saveHarness, loadHarness);

        Assert.ThatCollection(loadHarness.Errors).Has.Count(1);
        Assert.ThatCollection(loadHarness.Errors[0].Frames).Is.Empty();
    }
}
