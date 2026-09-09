using System.Net;
using DebugAssistance.Web.Dtos;

namespace DebugAssistance.Web;

// GET /api/settings: hands the frontend the subset of mod settings that affect what it should
// render, so those settings stay a single source of truth in Settings.cs rather than being
// duplicated as frontend-side defaults. POST /api/settings/error-capture-enabled: the one
// setting the frontend can also flip back, rather than only read - the error-capture toggle,
// persisted the same way the in-game settings window's checkbox would be.
internal static class SettingsEndpoint
{
    internal static bool ServeSettings(HttpListenerContext ctx)
    {
        ctx.Response.WriteJson(ToDto());
        return true;
    }

    internal static bool ServeSetErrorCaptureEnabled(HttpListenerContext ctx)
    {
        if (ctx.Request.ReadJson<ErrorCaptureToggleRequestDto>() is not { } body)
        {
            return ctx.Response.WriteJsonError(400, "Missing or invalid request body");
        }

        DebugAssistanceMod.Settings.ErrorCaptureEnabled = body.Enabled;
        DebugAssistanceMod.Settings.Write();

        ctx.Response.WriteJson(ToDto());
        return true;
    }

    private static SettingsResponseDto ToDto() =>
        new()
        {
            AiPromptGeneratorEnabled = DebugAssistanceMod.Settings.AiPromptGeneratorEnabled,
            ErrorCaptureEnabled = DebugAssistanceMod.Settings.ErrorCaptureEnabled,
        };
}
