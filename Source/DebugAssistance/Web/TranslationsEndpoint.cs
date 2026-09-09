using System.Net;
using System.Xml.Linq;
using DebugAssistance.Web.Dtos;

namespace DebugAssistance.Web;

// GET /api/translations: hands the frontend the active language's text for every
// DebugAssistance.FrontEnd.* key (declared in Common/Languages/English/Keyed/FrontEnd.xml, and any
// translation of it in another language present in this mod), so the web UI's own strings follow
// the player's chosen game language instead of being hard-coded in the frontend. Values keep any
// `{0}`-style placeholders as-is; the frontend fills those in itself for values that need runtime
// data (a count, a file name, a date).
internal static class TranslationsEndpoint
{
    private const string KeyPrefix = "DebugAssistance.FrontEnd.";

    // Keys are the FrontEnd.xml key's suffix after "DebugAssistance.FrontEnd."; the frontend looks
    // values up by that same suffix. Discovered from the English FrontEnd.xml itself rather than
    // hard-coded, and read only once since that file never changes after mod load.
    private static readonly Lazy<IReadOnlyList<string>> Keys = new(LoadKeys);

    private static IReadOnlyList<string> LoadKeys()
    {
        var path = Path.Combine(
            DebugAssistanceMod.Instance.Content.RootDir,
            "Common",
            "Languages",
            "English",
            "Keyed",
            "FrontEnd.xml"
        );
        return ExtractKeys(XDocument.Load(path));
    }

    internal static IReadOnlyList<string> ExtractKeys(XDocument frontEndXml) =>
        [
            .. frontEndXml
                .Root.Elements()
                .Select(element => element.Name.LocalName)
                .Where(name => name.StartsWith(KeyPrefix, StringComparison.Ordinal))
                .Select(name => name[KeyPrefix.Length..]),
        ];

    internal static bool ServeTranslations(HttpListenerContext ctx)
    {
        var translations = Keys.Value.ToDictionary(
            key => key,
            key => (string)$"{KeyPrefix}{key}".Translate()
        );

        ctx.Response.WriteJson(new TranslationsResponseDto { Translations = translations });
        return true;
    }
}
