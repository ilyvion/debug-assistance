using System.Net;
using DebugAssistance.Probes;
using DebugAssistance.PromptGeneration;
using DebugAssistance.Web.Dtos;

namespace DebugAssistance.Web;

// /api/probes/* routes: the active-probe list plus add/remove (ProbeManager), and the captured
// probe-hit list/detail/delete/clear (ProbeHitStore) -- the probe analogue of ErrorsEndpoints and
// (for its active-probes half) HotPatchEndpoints' active-patches routes.
internal static class ProbesEndpoints
{
    internal static IReadOnlyList<CapturedProbeHit> Entries() =>
        DebugAssistanceMod.ProbeHitStore.Snapshot();

    internal static bool ServeActiveProbes(HttpListenerContext ctx)
    {
        ctx.Response.WriteJson(
            new ActiveProbeListDto
            {
                Probes = [.. DebugAssistanceMod.ProbeManager.ActiveProbes.Select(ToDto)],
            }
        );
        return true;
    }

    internal static bool ServeAddProbe(HttpListenerContext ctx)
    {
        var body = ctx.Request.ReadJson<AddProbeRequestDto>();
        if (body?.Target is null)
        {
            return ctx.Response.WriteJsonError(400, "target is required");
        }

        var target = HotPatchEndpoints.ResolveMethod(
            HotPatchEndpoints.ResolveAssemblyByFullName(body.Target.AssemblyFullName),
            body.Target.MetadataToken
        );
        if (target is null)
        {
            return ctx.Response.WriteJsonError(404, "Target method could not be resolved");
        }

        var (probe, error) = DebugAssistanceMod.ProbeManager.AddProbe(target);
        ctx.Response.WriteJson(
            new AddProbeResultDto
            {
                Success = probe is not null,
                Error = error,
                Probe = probe is null ? null : ToDto(probe),
            }
        );
        return true;
    }

    internal static bool ServeRemoveActiveProbe(HttpListenerContext ctx, string id)
    {
        var removed = DebugAssistanceMod.ProbeManager.RemoveProbe(id);
        ctx.Response.WriteJson(new RemoveProbeResultDto { Removed = removed });
        return true;
    }

    internal static bool ServeProbeList(HttpListenerContext ctx)
    {
        var entries = Entries();
        ctx.Response.WriteJson(
            new ProbeListResponseDto
            {
                Probes = [.. entries.OrderByDescending(h => h.LastSeen).Select(ToListEntryJson)],
            }
        );
        return true;
    }

    internal static bool ServeProbeDetail(HttpListenerContext ctx, string dedupeKey)
    {
        if (ProbeRouteResolver.FindHit(Entries(), dedupeKey) is not { } hit)
        {
            return ctx.Response.WriteJsonError(404, "Probe hit not found");
        }

        ctx.Response.WriteJson(
            new ProbeDetailDto
            {
                DedupeKey = hit.DedupeKey,
                TargetDeclaringTypeName = hit.TargetDeclaringTypeName,
                TargetMethodName = hit.TargetMethodName,
                TargetDisplayName = hit.TargetDisplayName,
                RawStackTrace = hit.RawStackTrace,
                OccurrenceCount = hit.OccurrenceCount,
                FirstSeen = hit.FirstSeen.ToIsoString(),
                LastSeen = hit.LastSeen.ToIsoString(),
                Frames = [.. hit.Frames.Select(ErrorsEndpoints.ToFrameJson)],
            }
        );
        return true;
    }

    internal static bool ServeDeleteProbeHit(HttpListenerContext ctx, string dedupeKey)
    {
        if (!DebugAssistanceMod.ProbeHitStore.Remove(dedupeKey))
        {
            return ctx.Response.WriteJsonError(404, "Probe hit not found");
        }

        ctx.Response.WriteJson(new DeleteResultDto { Deleted = true });
        return true;
    }

    internal static bool ServeClearProbes(HttpListenerContext ctx)
    {
        var clearedCount = DebugAssistanceMod.ProbeHitStore.Clear();
        ctx.Response.WriteJson(new ClearProbesResultDto { ClearedCount = clearedCount });
        return true;
    }

    internal static bool ServeProbeAiPrompt(HttpListenerContext ctx, string dedupeKey)
    {
        if (ProbeRouteResolver.FindHit(Entries(), dedupeKey) is not { } hit)
        {
            return ctx.Response.WriteJsonError(404, "Probe hit not found");
        }

        ctx.Response.WriteJson(new AiPromptResultDto { Prompt = PromptTemplate.Build(hit) });
        return true;
    }

    private static ProbeListEntryDto ToListEntryJson(CapturedProbeHit hit) =>
        new()
        {
            DedupeKey = hit.DedupeKey,
            TargetDeclaringTypeName = hit.TargetDeclaringTypeName,
            TargetMethodName = hit.TargetMethodName,
            TargetDisplayName = hit.TargetDisplayName,
            OccurrenceCount = hit.OccurrenceCount,
            FirstSeen = hit.FirstSeen.ToIsoString(),
            LastSeen = hit.LastSeen.ToIsoString(),
            TopFrameModName = hit.Frames.Count > 0 ? hit.Frames[0].ResolvedModName : null,
        };

    private static ActiveProbeDto ToDto(ProbeDefinition probe) =>
        new()
        {
            Id = probe.Id,
            TargetDeclaringTypeName = probe.TargetMethod.DeclaringType?.FullName ?? "?",
            TargetMethodName = probe.TargetMethod.Name,
            TargetDisplayName = probe.TargetDisplayName,
            AppliedAt = probe.AppliedAt.ToIsoString(),
            TotalInvocationCount = probe.TotalInvocationCount,
            UniqueHitCount = probe.UniqueHitCount,
            IsActive = probe.IsActive,
            CapReason = probe.CapReason.ToString(),
        };
}
