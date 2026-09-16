using System.Net;
using DebugAssistance.PromptGeneration;
using DebugAssistance.Web.Dtos;

namespace DebugAssistance.Web;

// POST /api/errors/{dedupeKey}/ai-prompt and POST /api/hotpatch/debug-prompt: build the
// AI-prompt Markdown documents (PromptTemplate) the frontend copies verbatim to the clipboard via
// the browser's own Clipboard API rather than GUIUtility.systemCopyBuffer.
internal static class PromptEndpoints
{
    internal static bool ServeAiPrompt(HttpListenerContext ctx, string dedupeKey)
    {
        if (
            ErrorRouteResolver.FindError(DebugAssistanceServer.Entries(), dedupeKey)
            is not { } error
        )
        {
            return ctx.Response.WriteJsonError(404, "Error not found");
        }

        ctx.Response.WriteJson(new AiPromptResultDto { Prompt = PromptTemplate.Build(error) });
        return true;
    }

    // Reachable from the Hot Patch panel's "Patch this method" entry point off either a
    // captured error or a captured probe hit — the frontend hides this button entirely
    // otherwise, since there'd be nothing to attribute a debug prompt to — but
    // DedupeKey/ProjectDirectory are validated here regardless, the same as every other route
    // trusts nothing about its caller's UI state. A dedupeKey could in principle collide between
    // the two stores (each is hashed from unambiguously different inputs in practice, but
    // nothing enforces that), so the error store is checked first and wins if both match.
    internal static bool ServeHotPatchDebugPrompt(HttpListenerContext ctx)
    {
        var body = ctx.Request.ReadJson<HotPatchDebugPromptRequestDto>();
        if (
            body is null
            || string.IsNullOrWhiteSpace(body.DedupeKey)
            || string.IsNullOrWhiteSpace(body.ProjectDirectory)
        )
        {
            return ctx.Response.WriteJsonError(400, "dedupeKey and projectDirectory are required");
        }

        var target = body.Target is { } targetRef
            ? HotPatchEndpoints.ResolveMethod(
                HotPatchEndpoints.ResolveAssemblyByFullName(targetRef.AssemblyFullName),
                targetRef.MetadataToken
            )
            : null;
        var targetDescription = target is null ? null : HotPatchEndpoints.DescribeMethod(target);

        if (
            ErrorRouteResolver.FindError(DebugAssistanceServer.Entries(), body.DedupeKey) is
            { } error
        )
        {
            var prompt = PromptTemplate.BuildHotPatchPrompt(
                error,
                body.ProjectDirectory,
                targetDescription
            );
            ctx.Response.WriteJson(new AiPromptResultDto { Prompt = prompt });
            return true;
        }

        if (ProbeRouteResolver.FindHit(ProbesEndpoints.Entries(), body.DedupeKey) is { } hit)
        {
            var prompt = PromptTemplate.BuildHotPatchPrompt(
                hit,
                body.ProjectDirectory,
                targetDescription
            );
            ctx.Response.WriteJson(new AiPromptResultDto { Prompt = prompt });
            return true;
        }

        return ctx.Response.WriteJsonError(404, "Error not found");
    }
}
