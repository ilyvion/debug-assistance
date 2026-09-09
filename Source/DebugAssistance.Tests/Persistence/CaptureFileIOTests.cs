using DebugAssistance.Persistence;
using RimTestRedux;

namespace DebugAssistance.Tests.Persistence;

// Covers only the pure, no-I/O logic in CaptureFileIO — actual Save/Load against a real file,
// GetSavedFilesList, and the mod-mismatch confirmation dialog are UI/game-API bound and not
// unit-testable. The IExposable round trip that Save/Load rely on
// internally (Scribe_Collections.Look(..., LookMode.Deep)) is covered separately, against an
// in-memory stream, by CapturedErrorExposeDataTests.
[TestSuite]
internal static class CaptureFileIOTests
{
    [Test]
    public static void IsValidFileNameAcceptsAnOrdinaryName() =>
        Assert.That(CaptureFileIO.IsValidFileName("MyCapturedErrors")).Is.True();

    [Test]
    public static void IsValidFileNameRejectsPathSeparators() =>
        Assert.That(CaptureFileIO.IsValidFileName("in/valid")).Is.False();

    [Test]
    public static void SaveRejectsAnInvalidFileNameBeforeTouchingScribeOrDisk() =>
        Assert.ThatFunc(() => CaptureFileIO.Save([], "in/valid")).Throw();
}
