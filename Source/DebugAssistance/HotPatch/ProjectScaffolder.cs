using System.Text.RegularExpressions;
using DebugAssistance.Capture;

namespace DebugAssistance.HotPatch;

internal sealed record ScaffoldResult(
    bool Success,
    string? Error,
    string? ProjectDirectory = null,
    string? ExpectedAssemblyPath = null
);

// Generates a ready-to-build, fully self-contained classlib starter project so the player doesn't
// have to hand-assemble a .csproj before writing an on-the-fly patch. This project must never
// reference rimworld-utils, Directory.Build.props, globalusings.cs, or any other file from
// ilyvion's repo family -- a third-party player's machine has none of that checked out -- so
// every reference below is a plain public NuGet package, pinned to the same version this repo's
//  own Common.props uses, baked in as literal constants rather than read from that file at runtime.
internal static class ProjectScaffolder
{
    internal const string DefaultProjectName = "DebugAssistancePatch";

    // GenText.IsValidFilename rejects any name longer than this.
    private const int MaxProjectNameLength = 40;

    private const string TargetFramework = "net481";
    private const string RimWorldRefVersion = "1.6.*";
    private const string HarmonyVersion = "2.4.2";
    private const string PublicizerVersion = "2.2.1";
    private const string PolySharpVersion = "1.14.1";

    private static readonly Regex InvalidIdentifierCharsRegex = new(
        @"[^A-Za-z0-9_]+",
        RegexOptions.Compiled
    );

    // directoryPath is the parent location the player picked; the project itself is written into a
    // new projectName subdirectory created inside it (also projectName's own <ProjectName> and the
    // .csproj's file name), so a non-empty directoryPath is fine as long as that subdirectory
    // doesn't already exist. Refuses (without writing anything) if that subdirectory already
    // exists and isn't empty, or if projectName wouldn't make a valid file name.
    internal static ScaffoldResult Scaffold(
        string directoryPath,
        string projectName,
        MethodBase? targetMethod,
        CapturedError? context
    )
    {
        if (!GenText.IsValidFilename(projectName))
        {
            return new ScaffoldResult(false, $"\"{projectName}\" is not a valid project name.");
        }

        var projectDirectory = Path.Combine(directoryPath, projectName);
        if (
            Directory.Exists(projectDirectory)
            && Directory.EnumerateFileSystemEntries(projectDirectory).Any()
        )
        {
            return new ScaffoldResult(
                false,
                $"A folder named \"{projectName}\" already exists there and is not empty."
            );
        }

        _ = Directory.CreateDirectory(projectDirectory);

        File.WriteAllText(
            Path.Combine(projectDirectory, $"{projectName}.csproj"),
            BuildCsproj(targetMethod)
        );
        File.WriteAllText(Path.Combine(projectDirectory, "GlobalUsings.cs"), GlobalUsingsContent);
        File.WriteAllText(Path.Combine(projectDirectory, ".gitignore"), GitIgnoreContent);
        File.WriteAllText(
            Path.Combine(projectDirectory, "Patches.cs"),
            BuildStarterFile(targetMethod, context)
        );

        var expectedAssemblyPath = Path.Combine(
            projectDirectory,
            "bin",
            "Debug",
            TargetFramework,
            $"{projectName}.dll"
        );
        return new ScaffoldResult(true, null, projectDirectory, expectedAssemblyPath);
    }

    // A sanitized "<TargetType>_<TargetMethod>_<ErrorType>" when scaffolding from a captured
    // frame, so several scaffolded projects can be told apart at a glance; just the generic default
    // for the "arbitrary method" entry point, which has no frame to name the project after. Cut
    // down to MaxProjectNameLength when that would otherwise not be a valid project name.
    internal static string SuggestProjectName(MethodBase? targetMethod, CapturedError? context)
    {
        if (targetMethod is null)
        {
            return DefaultProjectName;
        }

        List<string> parts =
        [
            DefaultProjectName,
            SanitizeIdentifier(TypeName(targetMethod)),
            SanitizeIdentifier(targetMethod.Name),
        ];
        if (context is not null)
        {
            parts.Add(SanitizeIdentifier(ShortErrorTypeName(context.ErrorTypeName)));
        }

        var name = string.Join("_", parts);
        if (name.Length <= MaxProjectNameLength)
        {
            return name;
        }

        // Cut at a part boundary rather than mid-word; DefaultProjectName's own trailing "_"
        // guarantees at least one boundary within the limit to fall back to.
        var truncated = name[..MaxProjectNameLength];
        var lastBoundary = truncated.LastIndexOf('_');
        return lastBoundary > 0 ? truncated[..lastBoundary] : truncated;
    }

    private static string TypeName(MethodBase method) => method.DeclaringType?.Name ?? method.Name;

    private static string ShortErrorTypeName(string errorTypeName) =>
        errorTypeName.Contains('.', StringComparison.Ordinal)
            ? errorTypeName[(errorTypeName.LastIndexOf('.') + 1)..]
            : errorTypeName;

    private static string SanitizeIdentifier(string value) =>
        InvalidIdentifierCharsRegex.Replace(value, "_").Trim('_');

    private static string BuildCsproj(MethodBase? targetMethod)
    {
        List<string> lines =
        [
            "<Project Sdk=\"Microsoft.NET.Sdk\">",
            "    <PropertyGroup>",
            $"        <TargetFramework>{TargetFramework}</TargetFramework>",
            "        <LangVersion>latest</LangVersion>",
            "        <Nullable>enable</Nullable>",
            "        <ImplicitUsings>enable</ImplicitUsings>",
            "    </PropertyGroup>",
            "    <ItemGroup>",
            $"        <PackageReference Include=\"Krafs.Rimworld.Ref\" Version=\"{RimWorldRefVersion}\">",
            "            <ExcludeAssets>runtime</ExcludeAssets>",
            "        </PackageReference>",
            $"        <PackageReference Include=\"Lib.Harmony\" Version=\"{HarmonyVersion}\">",
            "            <ExcludeAssets>runtime</ExcludeAssets>",
            "        </PackageReference>",
            $"        <PackageReference Include=\"Krafs.Publicizer\" Version=\"{PublicizerVersion}\">",
            "            <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>",
            "            <PrivateAssets>all</PrivateAssets>",
            "        </PackageReference>",
            $"        <PackageReference Include=\"PolySharp\" Version=\"{PolySharpVersion}\">",
            "            <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>",
            "            <PrivateAssets>all</PrivateAssets>",
            "        </PackageReference>",
            "    </ItemGroup>",
        ];

        lines.AddRange(BuildThirdPartyReferenceItemGroup(targetMethod));
        lines.AddRange(BuildPublicizeItemGroup(targetMethod));
        lines.Add("</Project>");
        lines.Add("");

        return string.Join(Environment.NewLine, lines);
    }

    // The target's declaring type (and any third-party mod type reachable from its parameters or
    // return type -- including through generics, arrays, and by-ref) needs an explicit file
    // reference here, since Krafs.Rimworld.Ref/Lib.Harmony only cover the base game, Unity, the
    // BCL, and Harmony itself; without this, a target method belonging to (or touching) another
    // mod fails to compile in the scaffolded project. Private=false (MSBuild's "Copy Local")
    // stops that mod's assembly from being copied into the patch project's own output directory.
    private static List<string> BuildThirdPartyReferenceItemGroup(MethodBase? targetMethod)
    {
        var references = CollectThirdPartyReferenceAssemblies(targetMethod)
            .Select(assembly => (assembly.GetName().Name, Path: GetAssemblyFilePath(assembly)))
            .Where(reference => reference.Path is not null)
            .Select(reference => (reference.Name, Path: reference.Path!))
            .ToList();

        if (references.Count == 0)
        {
            return [];
        }

        List<string> lines = ["    <ItemGroup>"];
        foreach (var (name, path) in references)
        {
            lines.Add($"        <Reference Include=\"{EscapeXmlAttribute(name)}\">");
            lines.Add($"            <HintPath>{EscapeXmlAttribute(path)}</HintPath>");
            lines.Add("            <Private>false</Private>");
            lines.Add("        </Reference>");
        }
        lines.Add("    </ItemGroup>");
        return lines;
    }

    // A target type/parameter/return type that isn't visible outside its own assembly (internal,
    // or nested inside a non-public type) would otherwise fail to compile in the generated
    // Prefix/Postfix/etc. signatures, since Krafs.Rimworld.Ref only exposes RimWorld's public API
    // and a third-party mod's own assembly is referenced as-is -- Krafs.Publicizer's
    // IgnoresAccessChecksToAttribute mechanism is what makes those types compile-time accessible.
    private static List<string> BuildPublicizeItemGroup(MethodBase? targetMethod)
    {
        var assemblyNames = CollectNonPublicReferencedTypes(targetMethod)
            .Select(type => type.Assembly.GetName().Name)
            .Distinct()
            .ToList();

        if (assemblyNames.Count == 0)
        {
            return [];
        }

        List<string> lines = ["    <ItemGroup>"];
        foreach (var name in assemblyNames)
        {
            lines.Add($"        <Publicize Include=\"{EscapeXmlAttribute(name)}\" />");
        }
        lines.Add("    </ItemGroup>");
        return lines;
    }

    private static IEnumerable<Type> CollectNonPublicReferencedTypes(MethodBase? method)
    {
        if (method is null)
        {
            yield break;
        }

        HashSet<Type> seen = [];
        foreach (var type in ReferencedTypes(method).SelectMany(FlattenType))
        {
            if (!type.IsVisible && seen.Add(type))
            {
                yield return type;
            }
        }
    }

    private static IEnumerable<Assembly> CollectThirdPartyReferenceAssemblies(MethodBase? method)
    {
        if (method is null)
        {
            yield break;
        }

        HashSet<Assembly> seen = [];
        foreach (var type in ReferencedTypes(method).SelectMany(FlattenType))
        {
            if (NeedsExplicitReference(type.Assembly) && seen.Add(type.Assembly))
            {
                yield return type.Assembly;
            }
        }
    }

    private static IEnumerable<Type> ReferencedTypes(MethodBase method)
    {
        if (method.DeclaringType is { } declaringType)
        {
            yield return declaringType;
        }

        foreach (var parameter in method.GetParameters())
        {
            yield return StripByRef(parameter.ParameterType);
        }

        if (method is MethodInfo { ReturnType: var returnType } && returnType != typeof(void))
        {
            yield return returnType;
        }
    }

    // Walks into array/by-ref/pointer element types and generic type arguments (e.g. the
    // ThirdPartyDef in List<ThirdPartyDef>) so a mod type reached only that way still gets a
    // reference, not just a type used directly as a parameter/return type.
    private static IEnumerable<Type> FlattenType(Type type)
    {
        yield return type;

        if (type.HasElementType && type.GetElementType() is { } elementType)
        {
            foreach (var nested in FlattenType(elementType))
            {
                yield return nested;
            }
        }

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                foreach (var nested in FlattenType(argument))
                {
                    yield return nested;
                }
            }
        }
    }

    // Krafs.Rimworld.Ref and Lib.Harmony already cover everything from the base game, Unity, the
    // BCL, and Harmony itself.
    private static bool NeedsExplicitReference(Assembly assembly) =>
        assembly.GetName().Name != "0Harmony"
        && FrameModResolver.ClassifyFrameworkAssembly(assembly) is null;

    // Assembly.Location is populated by RimWorld 1.5+'s own Assembly.LoadFrom, but this mod also
    // supports 1.3/1.4, which instead load mod assemblies into memory (Assembly.Load(byte[])) and
    // leave it empty -- so this falls back to locating the same-named .dll under the owning mod's
    // own Assemblies folder(s), the same lookup ModAssemblyHandler.ReloadAll itself uses to find
    // them in the first place.
    private static string? GetAssemblyFilePath(Assembly assembly)
    {
        if (!string.IsNullOrEmpty(assembly.Location) && File.Exists(assembly.Location))
        {
            return assembly.Location;
        }

        var mod = LoadedModManager.RunningMods.FirstOrDefault(mod =>
            mod.assemblies.loadedAssemblies.Contains(assembly)
        );
        if (mod is null)
        {
            return null;
        }

        var assemblyName = assembly.GetName().Name;
        return ModContentPack
            .GetAllFilesForModPreserveOrder(
                mod,
                "Assemblies/",
                extension => string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase)
            )
            .Select(entry => entry.Item2)
            .FirstOrDefault(file => Path.GetFileNameWithoutExtension(file.Name) == assemblyName)
            ?.FullName;
    }

    private static string EscapeXmlAttribute(string value) =>
        value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    private const string GlobalUsingsContent = """
        global using HarmonyLib;
        global using RimWorld;
        global using UnityEngine;
        global using Verse;

        """;

    private const string GitIgnoreContent = """
        bin/
        obj/

        """;

    // When scaffolded from a specific captured frame (targetMethod/context both present), reflects
    // the target's actual parameters/return type/static-vs-instance-ness so Prefix/Postfix already
    // match the real signature instead of being generic placeholders; otherwise emits a generic
    // stub for the player to fill in by hand.
    private static string BuildStarterFile(MethodBase? targetMethod, CapturedError? context)
    {
        List<string> lines = [];

        var header = BuildHeaderComment(targetMethod, context);
        if (header.Length > 0)
        {
            lines.Add(header);
        }

        lines.AddRange([
            "namespace GeneratedPatch;",
            "",
            "// Delete whichever patch method(s) you don't need. Load the built DLL from",
            "// DebugAssistance's Hot Patch panel: it detects the [Harmony*] attributes below and",
            "// offers each one to apply directly, or pick target/patch method/patch type by hand.",
        ]);
        var classAttribute = BuildHarmonyPatchAttribute(targetMethod);
        if (classAttribute.Length > 0)
        {
            lines.Add(classAttribute);
        }
        lines.AddRange([
            "internal static class Patches",
            "{",
            "    // Runs before the original method. Returning false skips the original method",
            "    // entirely.",
            "    [HarmonyPrefix]",
            $"    internal static bool Prefix({BuildParameterList(targetMethod, includeResult: false)})",
            "    {",
            "        return true;",
            "    }",
            "",
            "    // Runs after the original method.",
            "    [HarmonyPostfix]",
            $"    internal static void Postfix({BuildParameterList(targetMethod, includeResult: true)})",
            "    {",
            "    }",
            "",
            "    // Rewrites the original method's IL. `instructions` is the original method body;",
            "    // return it unchanged to make no changes.",
            "    [HarmonyTranspiler]",
            "    internal static IEnumerable<CodeInstruction> Transpiler(",
            "        IEnumerable<CodeInstruction> instructions",
            "    )",
            "    {",
            "        return instructions;",
            "    }",
            "",
            "    // Runs after the original method (and after Postfix) even if it threw.",
            "    // `__exception` is the exception that was thrown, or null if the method completed",
            "    // normally; returning a non-null Exception replaces it, returning null swallows it.",
            "    [HarmonyFinalizer]",
            "    internal static Exception? Finalizer(Exception? __exception)",
            "    {",
            "        return __exception;",
            "    }",
            "}",
            "",
        ]);

        return string.Join(Environment.NewLine, lines);
    }

    // Emits the [HarmonyPatch(...)] class attribute reflecting the target method's own type and
    // name, so PatchAttributeScanner can auto-detect this project's built assembly as already
    // having a pre-configured target once it's (re)loaded. "" (and thus no attribute at all) when
    // there's no target to reflect it from -- the from-scratch entry point, which has nothing to
    // annotate with yet.
    private static string BuildHarmonyPatchAttribute(MethodBase? targetMethod)
    {
        if (targetMethod is null || targetMethod.DeclaringType is not { } declaringType)
        {
            return "";
        }

        var typeExpr = CSharpTypeFormatter.FormatType(declaringType);
        return targetMethod is ConstructorInfo
            ? $"[HarmonyPatch(typeof({typeExpr}), MethodType.Constructor)]"
            : $"[HarmonyPatch(typeof({typeExpr}), nameof({typeExpr}.{targetMethod.Name}))]";
    }

    private static string BuildHeaderComment(MethodBase? targetMethod, CapturedError? context)
    {
        if (context is null)
        {
            return "";
        }

        var message = context.Message.Replace('\r', ' ').Replace('\n', ' ');
        var methodPart = targetMethod is not null
            ? $" while patching {CSharpTypeFormatter.DescribeMethod(targetMethod)}"
            : "";
        return $"// Generated from a captured {context.ErrorTypeName}{methodPart}: {message}";
    }

    private static string BuildParameterList(MethodBase? method, bool includeResult)
    {
        if (method is null)
        {
            return "";
        }

        List<string> parts = [];
        if (!method.IsStatic)
        {
            parts.Add($"{CSharpTypeFormatter.FormatType(method.DeclaringType)} __instance");
        }

        parts.AddRange(
            method
                .GetParameters()
                .Select(parameter =>
                    (parameter.ParameterType.IsByRef ? "ref " : "")
                    + CSharpTypeFormatter.FormatType(StripByRef(parameter.ParameterType))
                    + " "
                    + parameter.Name
                )
        );

        if (
            includeResult
            && method is MethodInfo { ReturnType: var returnType }
            && returnType != typeof(void)
        )
        {
            parts.Add($"ref {CSharpTypeFormatter.FormatType(returnType)} __result");
        }

        return string.Join(", ", parts);
    }

    private static Type StripByRef(Type type) => type.IsByRef ? type.GetElementType() : type;
}
