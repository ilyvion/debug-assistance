using System.Net;

namespace DebugAssistance.Web;

// GET /api/files?path=&mode=&extension=: lists a server-side directory's immediate contents (or
// platform roots when path is omitted), via FileBrowserService, for the frontend file browser
// (6.2) that both the hot-patch assembly picker (6.5) and the scaffold destination picker (6.8)
// embed.
internal static class FileBrowserEndpoints
{
    internal static bool ServeFileList(HttpListenerContext ctx)
    {
        var query = ctx.Request.QueryString;

        var mode = query["mode"] switch
        {
            "dir" => FileBrowserMode.Directories,
            "file" or null => FileBrowserMode.Files,
            _ => (FileBrowserMode?)null,
        };

        if (mode is null)
        {
            return ctx.Response.WriteJsonError(400, "mode must be 'dir' or 'file'");
        }

        try
        {
            ctx.Response.WriteJson(
                FileBrowserService.List(query["path"], mode.Value, query["extension"])
            );
            return true;
        }
        catch (Exception ex)
            when (ex is UnauthorizedAccessException or IOException or ArgumentException)
        {
            return ctx.Response.WriteJsonError(400, ex.Message);
        }
    }
}
