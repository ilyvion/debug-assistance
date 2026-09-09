using DebugAssistance.Web;
using DebugAssistance.Web.Dtos;
using RimTestRedux;

namespace DebugAssistance.Tests.Web;

[TestSuite]
internal static class JsonSerializationTests
{
    [Test]
    public static void SerializeJsonCamelCasesDtoPropertyNames()
    {
        var json = DebugAssistanceServerExtensions.SerializeJson(new ErrorDto { Error = "boom" });

        Assert.That(json).Is.EqualTo("""{"error":"boom"}""");
    }

    [Test]
    public static void SerializeJsonLeavesDictionaryKeysUntouched()
    {
        // Translation keys (e.g. "ErrorDetail.DecompileAll") are looked up verbatim by the
        // frontend, so they must round-trip unchanged even though the enclosing DTO's own
        // property name ("Translations") still gets camelCased.
        var json = DebugAssistanceServerExtensions.SerializeJson(
            new TranslationsResponseDto
            {
                Translations = new Dictionary<string, string>
                {
                    ["ErrorDetail.DecompileAll"] = "Decompile all",
                },
            }
        );

        Assert
            .That(json)
            .Is.EqualTo("""{"translations":{"ErrorDetail.DecompileAll":"Decompile all"}}""");
    }
}
