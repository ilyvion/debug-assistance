using System.Xml.Linq;
using DebugAssistance.Web;
using RimTestRedux;

namespace DebugAssistance.Tests.Web;

[TestSuite]
internal static class TranslationsEndpointTests
{
    [Test]
    public static void ExtractKeysStripsTheDebugAssistanceFrontEndPrefix()
    {
        var xml = XDocument.Parse(
            """
            <LanguageData>
                <DebugAssistance.FrontEnd.Common.Unresolved>unresolved</DebugAssistance.FrontEnd.Common.Unresolved>
                <DebugAssistance.FrontEnd.App.FilterPlaceholder>Filter…</DebugAssistance.FrontEnd.App.FilterPlaceholder>
            </LanguageData>
            """
        );

        var keys = TranslationsEndpoint.ExtractKeys(xml);

        Assert.ThatCollection(keys).Has.Count(2);
        Assert.ThatCollection(keys).Does.Contain("Common.Unresolved");
        Assert.ThatCollection(keys).Does.Contain("App.FilterPlaceholder");
    }

    [Test]
    public static void ExtractKeysIgnoresElementsOutsideTheDebugAssistanceFrontEndNamespace()
    {
        // Guards against a stray or misspelled key silently ending up in the discovered set, since
        // this file is now the single source of truth for which keys the frontend can request.
        var xml = XDocument.Parse(
            """
            <LanguageData>
                <DebugAssistance.FrontEnd.Common.Unresolved>unresolved</DebugAssistance.FrontEnd.Common.Unresolved>
                <SomeOtherMod.Keyed.Thing>irrelevant</SomeOtherMod.Keyed.Thing>
            </LanguageData>
            """
        );

        var keys = TranslationsEndpoint.ExtractKeys(xml);

        Assert.ThatCollection(keys).Has.Count(1);
        Assert.ThatCollection(keys).Does.Contain("Common.Unresolved");
    }

    [Test]
    public static void ExtractKeysReturnsEmptyForAnEmptyLanguageData()
    {
        var xml = XDocument.Parse("<LanguageData></LanguageData>");

        var keys = TranslationsEndpoint.ExtractKeys(xml);

        Assert.ThatCollection(keys).Is.Empty();
    }
}
