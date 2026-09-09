using System.Net;
using DebugAssistance.Decompilation;
using DebugAssistance.Web.Dtos;

namespace DebugAssistance.Web;

// POST /api/errors/{dedupeKey}/frames/{frameIndex}/decompile[-patched] and
// .../patches/{patchIndex}/decompile: on-demand decompilation of a captured frame or one of the
// Harmony patches applied to it, via FrameDecompiler.
internal static class DecompileEndpoints
{
    internal static bool ServeDecompileFrame(
        HttpListenerContext ctx,
        string dedupeKey,
        string frameIndexPart,
        bool patched
    )
    {
        if (
            !ErrorRouteResolver.ResolveFrame(
                DebugAssistanceServer.Entries(),
                dedupeKey,
                frameIndexPart,
                out var frame,
                out var notFound
            )
        )
        {
            return ctx.Response.WriteJsonError(404, notFound!);
        }

        var result = patched
            ? FrameDecompiler.DecompilePatched(frame!)
            : FrameDecompiler.Decompile(frame!);
        WriteDecompileResult(ctx, result);
        return true;
    }

    internal static bool ServeDecompilePatch(
        HttpListenerContext ctx,
        string dedupeKey,
        string frameIndexPart,
        string patchIndexPart
    )
    {
        if (
            !ErrorRouteResolver.ResolveFrame(
                DebugAssistanceServer.Entries(),
                dedupeKey,
                frameIndexPart,
                out var frame,
                out var notFound
            )
        )
        {
            return ctx.Response.WriteJsonError(404, notFound!);
        }

        if (
            !int.TryParse(patchIndexPart, out var patchIndex)
            || patchIndex < 0
            || patchIndex >= frame!.Patches.Count
        )
        {
            return ctx.Response.WriteJsonError(404, "Patch not found");
        }

        WriteDecompileResult(ctx, FrameDecompiler.Decompile(frame.Patches[patchIndex]));
        return true;
    }

    private static void WriteDecompileResult(HttpListenerContext ctx, DecompiledMethod result)
    {
        if (result.Succeeded)
        {
            ctx.Response.WriteJson(
                new DecompileResultDto { Code = result.Code!, HighlightLine = result.HighlightLine }
            );
        }
        else
        {
            ctx.Response.WriteJson(new ErrorDto { Error = result.Error! });
        }
    }
}
