using DebugAssistance.Capture;

namespace DebugAssistance.Persistence;

// Save/load the full in-memory captured-error set to/from a player-chosen .dax file under
// GenFilePaths.FolderUnderSaveData("DebugAssistance"), following
// ManagerTab_ImportExport.cs's Scribe-against-an-arbitrary-path pattern. This is the plain
// persistence layer only, with no mod-mismatch confirmation around Load(...) — see
// Dialog_LoadExceptions.cs for why that's not needed here.
internal static class CaptureFileIO
{
    internal const string SaveExtension = ".dax";
    private const string RootNodeName = "DebugAssistanceCapture";

    internal static string SaveFolder =>
        GenFilePaths.FolderUnderSaveData(DebugAssistanceMod.PackageId);

    internal static string FilePathFor(string fileName) =>
        Path.Combine(SaveFolder, fileName + SaveExtension);

    internal static bool IsValidFileName(string fileName) => GenText.IsValidFilename(fileName);

    // Exports the entire current in-memory set (no per-item selection) to `fileName` under
    // SaveFolder, per the confirmed persistence design.
    internal static void Save(IEnumerable<CapturedError> errors, string fileName)
    {
        if (!IsValidFileName(fileName))
        {
            throw new ArgumentException(
                $"'{fileName}' is not a valid file name.",
                nameof(fileName)
            );
        }

        var list = errors.ToList();
        Scribe.saver.InitSaving(FilePathFor(fileName), RootNodeName);
        try
        {
            ScribeMetaHeaderUtility.WriteMetaHeader();
            // Scribe tag kept as "exceptions" so existing .dax capture files still load.
            Scribe_Collections.Look(ref list, "exceptions", LookMode.Deep);
        }
        finally
        {
            Scribe.saver.FinalizeSaving();
        }
    }

    // Reads the full set back out of `path`. Callers decide merge vs replace (CaptureStore.Merge
    // / .Replace) and, in the UI, whether to gate this behind a mod-mismatch confirmation.
    internal static List<CapturedError> Load(string path)
    {
        Scribe.loader.InitLoading(path);
        List<CapturedError> loaded = [];
        try
        {
            ScribeMetaHeaderUtility.LoadGameDataHeader(
                ScribeMetaHeaderUtility.ScribeHeaderMode.None,
                logVersionConflictWarning: true
            );
            Scribe_Collections.Look(ref loaded, "exceptions", LookMode.Deep);
            Scribe.loader.FinalizeLoading();
        }
        catch
        {
            Scribe.ForceStop();
            throw;
        }

        return loaded ?? [];
    }

    // Lists existing .dax files under SaveFolder, newest first, with RimWorld's own SaveFileInfo
    // metadata (game version, timestamp) for a load-file browser.
    internal static List<SaveFileInfo> GetSavedFilesList()
    {
        var directoryInfo = new DirectoryInfo(SaveFolder);
        if (!directoryInfo.Exists)
        {
            return [];
        }

        var files =
            from f in directoryInfo.GetFiles()
            where f.Extension == SaveExtension
            orderby f.LastWriteTime descending
            select f;

        List<SaveFileInfo> saves = [];
        foreach (var file in files)
        {
            try
            {
                var saveFileInfo = new SaveFileInfo(file);
                saveFileInfo.LoadData();
                saves.Add(saveFileInfo);
            }
            catch (Exception ex)
            {
                DebugAssistanceMod.Instance.LogError($"Exception loading {file.Name}: {ex}");
            }
        }

        return saves;
    }
}
