using System.Runtime.InteropServices;
using DebugAssistance.Web.Dtos;

namespace DebugAssistance.Web;

internal enum FileBrowserMode
{
    Directories,
    Files,
}

// Backs GET /api/files: a browser page never has access to the player's local filesystem paths —
// only a picked file's contents, never its on-disk path — so the assembly-path picker and
// scaffold-destination picker both drive this instead of a native OS file dialog. A directory
// this mod can't read (permission-denied, or one that vanished mid-session) yields an empty entry
// list for that directory rather than throwing and failing the whole request — mirrors
// MethodBrowser's ReflectionTypeLoadException tolerance for a partially-unreadable assembly.
internal static class FileBrowserService
{
    internal static FileBrowserListDto List(
        string? path,
        FileBrowserMode mode,
        string? extensionFilter
    )
    {
        var shortcuts = Shortcuts();

        if (path is null)
        {
            return new FileBrowserListDto
            {
                CurrentPath = null,
                ParentPath = null,
                Entries = [.. PlatformRoots()],
                Shortcuts = shortcuts,
            };
        }

        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath))
        {
            // A file path (e.g. one typed into the "Go" field, or a previously-picked file
            // reopened for browsing) resolves to its containing directory, listed with that file
            // visible among its entries, rather than being rejected as "not a directory".
            fullPath = Path.GetDirectoryName(fullPath) ?? fullPath;
        }
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Path not found: {fullPath}");
        }

        List<FileBrowserEntryDto> entries = [];

        foreach (
            var dir in SafeEnumerate(() => Directory.GetDirectories(fullPath))
                .OrderBy(dir => dir, StringComparer.OrdinalIgnoreCase)
        )
        {
            entries.Add(
                new FileBrowserEntryDto
                {
                    Name = Path.GetFileName(dir),
                    Path = dir,
                    IsDirectory = true,
                }
            );
        }

        if (mode == FileBrowserMode.Files)
        {
            foreach (
                var file in SafeEnumerate(() => Directory.GetFiles(fullPath))
                    .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            )
            {
                if (
                    extensionFilter is not null
                    && !Path.GetExtension(file)
                        .Equals(extensionFilter, StringComparison.OrdinalIgnoreCase)
                )
                {
                    continue;
                }

                if (TryDescribeFile(file) is { } entry)
                {
                    entries.Add(entry);
                }
            }
        }

        return new FileBrowserListDto
        {
            CurrentPath = fullPath,
            ParentPath = Directory.GetParent(fullPath)?.FullName,
            Entries = entries,
            Shortcuts = shortcuts,
        };
    }

    // Quick-jump shortcuts shown alongside the breadcrumb trail, so the player doesn't have to
    // click their way down from a platform root every time. The workshop path is derived from an
    // actually-installed workshop mod's own RootDir rather than a hardcoded Steam library path, so
    // it still resolves correctly under non-standard Steam installs (e.g. Flatpak).
    private static List<FileBrowserShortcutDto> Shortcuts()
    {
        List<FileBrowserShortcutDto> shortcuts = [];

        if (GenFilePaths.ModsFolderPath is { } modsFolderPath && Directory.Exists(modsFolderPath))
        {
            shortcuts.Add(new FileBrowserShortcutDto { Kind = "Mods", Path = modsFolderPath });
        }

        var workshopMod = ModLister.AllInstalledMods.FirstOrDefault(mod => mod.OnSteamWorkshop);
        if (workshopMod?.RootDir.Parent is { Exists: true } workshopContentDir)
        {
            shortcuts.Add(
                new FileBrowserShortcutDto { Kind = "Workshop", Path = workshopContentDir.FullName }
            );
        }

        return shortcuts;
    }

    // Windows exposes several roots (one per drive); Linux/macOS have exactly one, "/".
    private static IEnumerable<FileBrowserEntryDto> PlatformRoots()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (TryIsReady(drive))
                {
                    yield return new FileBrowserEntryDto
                    {
                        Name = drive.Name,
                        Path = drive.RootDirectory.FullName,
                        IsDirectory = true,
                    };
                }
            }
        }
        else
        {
            yield return new FileBrowserEntryDto
            {
                Name = "/",
                Path = "/",
                IsDirectory = true,
            };
        }
    }

    // An unready removable/network drive (e.g. an empty CD-ROM drive) throws IOException on
    // IsReady itself — skip just that drive rather than failing the whole root listing.
    private static bool TryIsReady(DriveInfo drive)
    {
        try
        {
            return drive.IsReady;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string[] SafeEnumerate(Func<string[]> list)
    {
        try
        {
            return list();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    private static FileBrowserEntryDto? TryDescribeFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return new FileBrowserEntryDto
            {
                Name = info.Name,
                Path = info.FullName,
                IsDirectory = false,
                Size = info.Length,
                LastWriteTime = info.LastWriteTimeUtc.ToIsoString(),
            };
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
