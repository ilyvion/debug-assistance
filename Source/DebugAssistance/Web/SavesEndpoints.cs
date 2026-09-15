using System.Net;
using DebugAssistance.Persistence;
using DebugAssistance.Web.Dtos;

namespace DebugAssistance.Web;

// GET /api/saves, POST /api/saves, POST /api/saves/{fileName}/load and DELETE /api/saves/{fileName}:
// listing, saving, loading (merge or replace), and deleting captured-error save files, via
// CaptureFileIO and DebugAssistanceMod.CaptureStore. Every save/load/delete also carries captured
// probe hits along in a companion ProbeFileIO file under the same base file name, so probe hits
// survive save/load the same way captured errors do without needing a save flow of their own.
internal static class SavesEndpoints
{
    internal static bool ServeSavesList(HttpListenerContext ctx)
    {
        var saves = CaptureFileIO.GetSavedFilesList();
        ctx.Response.WriteJson(
            new SavesListResponseDto
            {
                Saves =
                [
                    .. saves.Select(save => new SaveFileEntryDto
                    {
                        FileName = Path.GetFileNameWithoutExtension(save.FileInfo.Name),
                        LastWriteTime = save.LastWriteTime.ToIsoString(),
                    }),
                ],
            }
        );
        return true;
    }

    internal static bool ServeSaveCapture(HttpListenerContext ctx)
    {
        var body = ctx.Request.ReadJson<SaveCaptureRequestDto>();
        var fileName = body?.FileName;

        if (!SavesRequestValidator.IsValidFileName(fileName))
        {
            return ctx.Response.WriteJsonError(400, "Invalid file name");
        }

        if (
            SavesRequestValidator.ConflictsWithExistingSave(
                body!.Overwrite,
                File.Exists(CaptureFileIO.FilePathFor(fileName))
            )
        )
        {
            return ctx.Response.WriteJsonError(409, "A save with that name already exists");
        }

        try
        {
            var snapshot = DebugAssistanceMod.CaptureStore.Snapshot();
            CaptureFileIO.Save(snapshot, fileName);
            ProbeFileIO.Save(DebugAssistanceMod.ProbeHitStore.Snapshot(), fileName);
            ctx.Response.WriteJson(new SaveCaptureResultDto { OccurrenceCount = snapshot.Count });
            return true;
        }
        catch (Exception ex)
        {
            DebugAssistanceMod.Instance.LogError($"Exception saving captured errors: {ex}");
            return ctx.Response.WriteJsonError(500, ex.Message);
        }
    }

    internal static bool ServeLoadCapture(HttpListenerContext ctx, string fileName)
    {
        if (!SavesRequestValidator.IsValidFileName(fileName))
        {
            return ctx.Response.WriteJsonError(400, "Invalid file name");
        }

        var path = CaptureFileIO.FilePathFor(fileName);
        if (!File.Exists(path))
        {
            return ctx.Response.WriteJsonError(404, "Save file not found");
        }

        var mode = ctx.Request.ReadJson<LoadCaptureRequestDto>()?.Mode;
        if (!SavesRequestValidator.IsValidLoadMode(mode))
        {
            return ctx.Response.WriteJsonError(400, "mode must be 'merge' or 'replace'");
        }

        try
        {
            var loaded = CaptureFileIO.Load(path);
            if (mode == "replace")
            {
                DebugAssistanceMod.CaptureStore.Replace(loaded);
            }
            else
            {
                DebugAssistanceMod.CaptureStore.Merge(loaded);
            }

            // The companion probes file is a later addition, so an older save made before probes
            // existed simply has none — nothing to load, not an error.
            var probesPath = ProbeFileIO.FilePathFor(fileName);
            if (File.Exists(probesPath))
            {
                var loadedProbeHits = ProbeFileIO.Load(probesPath);
                if (mode == "replace")
                {
                    DebugAssistanceMod.ProbeHitStore.Replace(loadedProbeHits);
                }
                else
                {
                    DebugAssistanceMod.ProbeHitStore.Merge(loadedProbeHits);
                }
            }

            ctx.Response.WriteJson(new LoadCaptureResultDto { LoadedCount = loaded.Count });
            return true;
        }
        catch (Exception ex)
        {
            DebugAssistanceMod.Instance.LogError($"Exception loading captured errors: {ex}");
            return ctx.Response.WriteJsonError(500, ex.Message);
        }
    }

    internal static bool ServeDeleteSave(HttpListenerContext ctx, string fileName)
    {
        if (!SavesRequestValidator.IsValidFileName(fileName))
        {
            return ctx.Response.WriteJsonError(400, "Invalid file name");
        }

        var path = CaptureFileIO.FilePathFor(fileName);
        if (!File.Exists(path))
        {
            return ctx.Response.WriteJsonError(404, "Save file not found");
        }

        File.Delete(path);

        var probesPath = ProbeFileIO.FilePathFor(fileName);
        if (File.Exists(probesPath))
        {
            File.Delete(probesPath);
        }

        ctx.Response.WriteJson(new DeleteResultDto { Deleted = true });
        return true;
    }
}
