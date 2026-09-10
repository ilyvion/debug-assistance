using System.Net;
using DebugAssistance.Capture;
using DebugAssistance.Decompilation;
using DebugAssistance.HotPatch;
using DebugAssistance.Web.Dtos;

namespace DebugAssistance.Web;

// GET /api/errors and GET /api/errors/{dedupeKey}: the captured-error list and a single
// error's full frame/patch detail; DELETE /api/errors/{dedupeKey} and DELETE /api/errors
// dismiss a single entry or clear the whole store; plus the DTO-shaping helpers reused by
// DecompileEndpoints' frame/patch responses.
internal static class ErrorsEndpoints
{
    internal static bool ServeErrorList(HttpListenerContext ctx)
    {
        var entries = DebugAssistanceServer.Entries();
        ctx.Response.WriteJson(
            new ErrorListResponseDto
            {
                Errors = [.. entries.OrderByDescending(e => e.LastSeen).Select(ToListEntryJson)],
            }
        );
        return true;
    }

    internal static ErrorListEntryDto ToListEntryJson(CapturedError error) =>
        new()
        {
            DedupeKey = error.DedupeKey,
            ErrorTypeName = error.ErrorTypeName,
            Message = error.Message,
            OccurrenceCount = error.OccurrenceCount,
            FirstSeen = error.FirstSeen.ToIsoString(),
            LastSeen = error.LastSeen.ToIsoString(),
            HarmonyRefHash = error.HarmonyRefHash,
            TopFrameModName = error.Frames.Count > 0 ? error.Frames[0].ResolvedModName : null,
        };

    internal static bool ServeErrorDetail(HttpListenerContext ctx, string dedupeKey)
    {
        if (
            ErrorRouteResolver.FindError(DebugAssistanceServer.Entries(), dedupeKey)
            is not { } error
        )
        {
            return ctx.Response.WriteJsonError(404, "Error not found");
        }

        ctx.Response.WriteJson(
            new ErrorDetailDto
            {
                DedupeKey = error.DedupeKey,
                ErrorTypeName = error.ErrorTypeName,
                Message = error.Message,
                RawStackTrace = error.RawStackTrace,
                OccurrenceCount = error.OccurrenceCount,
                FirstSeen = error.FirstSeen.ToIsoString(),
                LastSeen = error.LastSeen.ToIsoString(),
                HarmonyRefHash = error.HarmonyRefHash,
                Frames = [.. error.Frames.Select(ToFrameJson)],
                InnerCauses = [.. error.InnerCauses.Select(ToCauseJson)],
            }
        );
        return true;
    }

    internal static bool ServeDeleteError(HttpListenerContext ctx, string dedupeKey)
    {
        if (!DebugAssistanceMod.CaptureStore.Remove(dedupeKey))
        {
            return ctx.Response.WriteJsonError(404, "Error not found");
        }

        ctx.Response.WriteJson(new DeleteResultDto { Deleted = true });
        return true;
    }

    internal static bool ServeClearErrors(HttpListenerContext ctx)
    {
        var clearedCount = DebugAssistanceMod.CaptureStore.Clear();
        ctx.Response.WriteJson(new ClearErrorsResultDto { ClearedCount = clearedCount });
        return true;
    }

    internal static ErrorCauseDto ToCauseJson(CapturedExceptionCause cause) =>
        new()
        {
            ErrorTypeName = cause.ErrorTypeName,
            Message = cause.Message,
            RawStackTrace = cause.RawStackTrace,
            Frames = [.. cause.Frames.Select(ToFrameJson)],
        };

    internal static FrameDto ToFrameJson(CapturedStackFrame frame, int index) =>
        new()
        {
            Index = index,
            RawText = frame.RawText,
            DeclaringTypeName = frame.DeclaringTypeName,
            MethodName = frame.MethodName,
            FileName = frame.FileName,
            LineNumber = frame.LineNumber,
            ColumnNumber = frame.ColumnNumber,
            IlOffset = frame.IlOffset,
            ResolvedModName = frame.ResolvedModName,
            ResolvedAssemblyShortName = frame.ResolvedAssemblyShortName,
            Patches = [.. frame.Patches.Select(ToPatchJson)],
            PatchTarget = ResolvePatchTarget(frame),
        };

    // Reuses FrameDecompiler's own frame -> live MethodBase resolution (live reference -> resolved
    // assembly short name -> type-name search) so a hot-patch target is offered whenever
    // decompiling the frame would also succeed, and withheld under that same "not currently
    // loaded"/unresolvable fallback rather than a separate resolution path of its own.
    internal static BrowsedMethodDto? ResolvePatchTarget(CapturedStackFrame frame) =>
        FrameDecompiler.TryResolveMethodForDecompile(
            frame.Assembly,
            frame.ResolvedAssemblyShortName,
            frame.DeclaringTypeName,
            frame.MethodName,
            out var method,
            out var assembly,
            out _,
            out _
        ) && method.DeclaringType is { } declaringType
            ? HotPatchEndpoints.ToDto(new BrowsedMethod(assembly, declaringType, method))
            : null;

    internal static PatchDto ToPatchJson(CapturedPatchFrame patch, int index) =>
        new()
        {
            Index = index,
            OwnerModId = patch.OwnerModId,
            PatchKind = patch.PatchKind,
            DeclaringTypeName = patch.DeclaringTypeName,
            MethodName = patch.MethodName,
        };
}
