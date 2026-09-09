using System.Net;
using System.Text;
using DebugAssistance.Web.Dtos;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace DebugAssistance.Web;

// HttpListenerResponse byte-writing and URL path-splitting helpers, plus the JSON read/write
// helpers every route DTO goes through, so the wire format (camelCase property names) lives in
// one place instead of being repeated per DTO.
internal static class DebugAssistanceServerExtensions
{
    // ProcessDictionaryKeys defaults to true on this resolver, which would also camelCase the keys
    // of TranslationsResponseDto's dictionary (the translation keys themselves, e.g.
    // "ErrorDetail.DecompileAll") — those must round-trip byte-for-byte since the frontend
    // looks them up by exact key, so dictionary keys are left alone; only property names are cased.
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver
        {
            NamingStrategy = new CamelCaseNamingStrategy { ProcessDictionaryKeys = false },
        },
    };

    internal static void WriteBytes(this HttpListenerResponse resp, byte[] data)
    {
        resp.ContentLength64 = data.LongLength;
        resp.OutputStream.Write(data, 0, data.Length);
    }

    internal static void WriteFile(this HttpListenerResponse resp, string path, byte[] data)
    {
        resp.ContentType = Path.GetExtension(path).ToUpperInvariant() switch
        {
            ".HTML" => "text/html",
            ".CSS" => "text/css",
            ".JS" => "application/javascript",
            ".JSON" => "application/json",
            ".SVG" => "image/svg+xml",
            ".PNG" => "image/png",
            ".JPG" or ".JPEG" => "image/jpeg",
            ".GIF" => "image/gif",
            ".ICO" => "image/x-icon",
            ".TXT" => "text/plain",
            _ => "application/octet-stream",
        };
        resp.WriteBytes(data);
    }

    // Split out from WriteJson so the wire format itself (property casing vs. dictionary-key
    // casing) can be unit-tested without a real HttpListenerResponse to write bytes into.
    internal static string SerializeJson<T>(T dto) =>
        JsonConvert.SerializeObject(dto, JsonSettings);

    internal static void WriteJson<T>(this HttpListenerResponse resp, T dto)
    {
        var data = Encoding.UTF8.GetBytes(SerializeJson(dto));
        resp.ContentType = "application/json";
        resp.ContentEncoding = Encoding.UTF8;
        resp.ContentLength64 = data.LongLength;
        resp.OutputStream.Write(data, 0, data.Length);
    }

    // Every API route's failure path sets a status code, writes a `{ error }` body, then returns
    // false to its own caller — folding all three into one call keeps that pattern to a single line
    // at each of the route handlers' many call sites.
    internal static bool WriteJsonError(
        this HttpListenerResponse resp,
        int statusCode,
        string error
    )
    {
        resp.StatusCode = statusCode;
        resp.WriteJson(new ErrorDto { Error = error });
        return false;
    }

    internal static string[] PathParts(this Uri url) =>
        url.AbsolutePath.Split(['/'], StringSplitOptions.RemoveEmptyEntries);

    // Returns default(T) (null for any reference-typed request DTO) for an empty body, since a few
    // routes (e.g. DELETE) never send one.
    internal static T? ReadJson<T>(this HttpListenerRequest req)
    {
        using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
        var body = reader.ReadToEnd();
        return body.Trim().Length == 0
            ? default
            : JsonConvert.DeserializeObject<T>(body, JsonSettings);
    }

    internal static string ToIsoString(this DateTime value) =>
        value.ToString("o", CultureInfo.InvariantCulture);
}
