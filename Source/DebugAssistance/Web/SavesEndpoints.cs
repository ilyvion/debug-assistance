using System.Net;
using DebugAssistance.Persistence;
using DebugAssistance.Web.Dtos;

namespace DebugAssistance.Web;

// GET /api/saves, POST /api/saves, POST /api/saves/{fileName}/load and DELETE /api/saves/{fileName}:
// listing, saving, loading (merge or replace), and deleting captured-error save files, via
// CaptureFileIO and DebugAssistanceMod.CaptureStore.
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
        ctx.Response.WriteJson(new DeleteResultDto { Deleted = true });
        return true;
    }
}
