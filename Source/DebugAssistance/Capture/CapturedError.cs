using System.Security.Cryptography;
using System.Text;

namespace DebugAssistance.Capture;

internal sealed class CapturedError : IExposable
{
    // IDE0032 ("use auto property") doesn't account for these being reassigned via `ref` from
    // ExposeData — an auto property's backing field isn't addressable that way.
#pragma warning disable IDE0032
    private string _errorTypeName;
    private string _message;
    private string _rawStackTrace;
#pragma warning restore IDE0032
    private List<CapturedStackFrame> _frames = [];
    private List<CapturedExceptionCause> _innerCauses = [];
    private int _occurrenceCount;

    // Stable across process restarts (unlike string.GetHashCode, which is randomized per-process)
    // so it survives round-tripping through save/load for merge-vs-replace dedup.
#pragma warning disable IDE0032
    private string _dedupeKey;
#pragma warning restore IDE0032

    // Parsed from a HarmonyMod "[Ref {hash:X}]" log prefix when present, purely so the player can
    // cross-reference the vanilla debug log. Display-only — does not participate in dedup.
    private int? _harmonyRefHash;

    public string ErrorTypeName => _errorTypeName;
    public string Message => _message;
    public string RawStackTrace => _rawStackTrace;
    public IReadOnlyList<CapturedStackFrame> Frames => _frames;

    // e.InnerException, e.InnerException.InnerException, etc. from the moment this error was
    // captured live, outermost-inner-first. Empty when the exception had no InnerException, or the
    // entry only ever reached us as parsed log text (no live Exception object to walk).
    public IReadOnlyList<CapturedExceptionCause> InnerCauses => _innerCauses;
    public DateTime FirstSeen { get; private set; }
    public DateTime LastSeen { get; private set; }
    public int OccurrenceCount => _occurrenceCount;
    public string DedupeKey => _dedupeKey;

    public int? HarmonyRefHash
    {
        get => _harmonyRefHash;
        set => _harmonyRefHash = value;
    }

    public CapturedError(
        string errorTypeName,
        string message,
        string rawStackTrace,
        IReadOnlyList<CapturedStackFrame> frames,
        DateTime timestamp,
        IReadOnlyList<CapturedExceptionCause>? innerCauses = null
    )
    {
        _errorTypeName = errorTypeName;
        _message = message;
        _rawStackTrace = rawStackTrace;
        _frames = [.. frames];
        _innerCauses = innerCauses is null ? [] : [.. innerCauses];
        FirstSeen = timestamp;
        LastSeen = timestamp;
        _occurrenceCount = 1;
        _dedupeKey = ComputeDedupeKey(errorTypeName, message, rawStackTrace, _innerCauses);
    }

#pragma warning disable CS8618 // Only for scribing
    private CapturedError() { }
#pragma warning restore CS8618

    internal void RecordOccurrence(DateTime timestamp)
    {
        _occurrenceCount++;
        if (timestamp > LastSeen)
        {
            LastSeen = timestamp;
        }
    }

    // Folds a loaded duplicate's historical data into this already-in-memory entry, for the
    // merge-load flow (CaptureStore.Merge) — distinct from RecordOccurrence, which represents
    // exactly one new live occurrence rather than a whole saved history being folded back in.
    internal void MergeFrom(CapturedError other)
    {
        _occurrenceCount += other._occurrenceCount;
        if (other.FirstSeen < FirstSeen)
        {
            FirstSeen = other.FirstSeen;
        }
        if (other.LastSeen > LastSeen)
        {
            LastSeen = other.LastSeen;
        }
        _harmonyRefHash ??= other._harmonyRefHash;
    }

    // innerCauses is folded into the hash so two entries whose outer wrapper is identical but
    // whose actual root cause differs (e.g. two different mods' HarmonyException-wrapped
    // TargetMethod() failures) don't dedupe into a single entry.
    internal static string ComputeDedupeKey(
        string errorTypeName,
        string message,
        string rawStackTrace,
        IReadOnlyList<CapturedExceptionCause>? innerCauses = null
    )
    {
        var parts = new List<string> { errorTypeName, message, rawStackTrace };
        if (innerCauses is not null)
        {
            foreach (var cause in innerCauses)
            {
                parts.Add(cause.ErrorTypeName);
                parts.Add(cause.Message);
                parts.Add(cause.RawStackTrace);
            }
        }
        var bytes = Encoding.UTF8.GetBytes(string.Join("\n", parts));
        using var sha256 = SHA256.Create();
        return BitConverter
            .ToString(sha256.ComputeHash(bytes))
            .Replace("-", "", StringComparison.Ordinal);
    }

    public void ExposeData()
    {
        // Scribe tag kept as "exceptionTypeName" so existing .dax capture files still load.
        Scribe_Values.Look(ref _errorTypeName!, "exceptionTypeName");
        Scribe_Values.Look(ref _message!, "message");
        Scribe_Values.Look(ref _rawStackTrace!, "rawStackTrace");
        Scribe_Collections.Look(ref _frames, "frames", LookMode.Deep);
        Scribe_Collections.Look(ref _innerCauses, "innerCauses", LookMode.Deep);

        // Verse's ParseHelper has no built-in DateTime parser, so Scribe_Values.Look can't
        // round-trip a DateTime directly - scribe ticks instead (same pattern as
        // ilyvion.Laboratory's IlyvionsLaboratorySettings.ExposeData).
        var firstSeenTicks = FirstSeen.Ticks;
        Scribe_Values.Look(ref firstSeenTicks, "firstSeen");
        FirstSeen = new DateTime(firstSeenTicks);

        var lastSeenTicks = LastSeen.Ticks;
        Scribe_Values.Look(ref lastSeenTicks, "lastSeen");
        LastSeen = new DateTime(lastSeenTicks);

        Scribe_Values.Look(ref _occurrenceCount, "occurrenceCount");
        Scribe_Values.Look(ref _dedupeKey!, "dedupeKey");
        Scribe_Values.Look(ref _harmonyRefHash, "harmonyRefHash");

        if (Scribe.mode == LoadSaveMode.LoadingVars)
        {
            _frames ??= [];
            _innerCauses ??= [];
        }
    }
}
