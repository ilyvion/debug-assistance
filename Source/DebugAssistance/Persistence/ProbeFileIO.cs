using DebugAssistance.Probes;

namespace DebugAssistance.Persistence;

// Save/load the full in-memory captured-probe-hit set to/from a player-chosen .dax file, alongside
// (same folder, same base file name, its own extension) whatever CaptureFileIO saves/loads for
// captured errors — see SavesEndpoints, which drives both together from the single Save/Load
// dialog. Structured identically to CaptureFileIO, just for CapturedProbeHit.
internal static class ProbeFileIO
{
    private const string ProbeFileSuffix = ".probes";
    private const string RootNodeName = "DebugAssistanceProbeCapture";

    internal static string FilePathFor(string fileName) =>
        Path.Combine(
            CaptureFileIO.SaveFolder,
            fileName + ProbeFileSuffix + CaptureFileIO.SaveExtension
        );

    internal static void Save(IEnumerable<CapturedProbeHit> hits, string fileName)
    {
        if (!CaptureFileIO.IsValidFileName(fileName))
        {
            throw new ArgumentException(
                $"'{fileName}' is not a valid file name.",
                nameof(fileName)
            );
        }

        var list = hits.ToList();
        Scribe.saver.InitSaving(FilePathFor(fileName), RootNodeName);
        try
        {
            ScribeMetaHeaderUtility.WriteMetaHeader();
            Scribe_Collections.Look(ref list, "probeHits", LookMode.Deep);
        }
        finally
        {
            Scribe.saver.FinalizeSaving();
        }
    }

    // Callers decide merge vs replace (ProbeHitStore.Merge / .Replace), mirroring
    // CaptureFileIO.Load.
    internal static List<CapturedProbeHit> Load(string path)
    {
        Scribe.loader.InitLoading(path);
        List<CapturedProbeHit> loaded = [];
        try
        {
            ScribeMetaHeaderUtility.LoadGameDataHeader(
                ScribeMetaHeaderUtility.ScribeHeaderMode.None,
                logVersionConflictWarning: true
            );
            Scribe_Collections.Look(ref loaded, "probeHits", LookMode.Deep);
            Scribe.loader.FinalizeLoading();
        }
        catch
        {
            Scribe.ForceStop();
            throw;
        }

        return loaded ?? [];
    }
}
