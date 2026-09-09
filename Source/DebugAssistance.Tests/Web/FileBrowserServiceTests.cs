using System.Diagnostics;
using DebugAssistance.Web;
using RimTestRedux;

namespace DebugAssistance.Tests.Web;

[TestSuite]
internal static class FileBrowserServiceTests
{
    private static string CreateFixtureTree()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "DebugAssistanceTests",
            $"FileBrowser_{Guid.NewGuid():N}"
        );
        _ = Directory.CreateDirectory(Path.Combine(root, "Zeta"));
        _ = Directory.CreateDirectory(Path.Combine(root, "Alpha"));
        File.WriteAllText(Path.Combine(root, "b.txt"), "b");
        File.WriteAllText(Path.Combine(root, "a.dll"), "a");
        return root;
    }

    [Test]
    public static void ListWithNoPathReturnsPlatformRoots()
    {
        var result = FileBrowserService.List(null, FileBrowserMode.Files, null);

        Assert.That(result.CurrentPath is null).Is.True();
        Assert.That(result.ParentPath is null).Is.True();
        // This test suite only ever runs on this project's Linux dev/CI machine, which has
        // exactly one root, "/" — see FileBrowserService.PlatformRoots.
        Assert.ThatCollection(result.Entries).Has.Count(1);
        Assert.That(result.Entries[0].Path).Is.EqualTo("/");
        Assert.That(result.Entries[0].IsDirectory).Is.True();
    }

    [Test]
    public static void ListOrdersDirectoriesBeforeFilesEachAlphabetically()
    {
        var root = CreateFixtureTree();

        var result = FileBrowserService.List(root, FileBrowserMode.Files, null);

        Assert.ThatCollection(result.Entries).Has.Count(4);
        Assert.That(result.Entries[0].Name).Is.EqualTo("Alpha");
        Assert.That(result.Entries[0].IsDirectory).Is.True();
        Assert.That(result.Entries[1].Name).Is.EqualTo("Zeta");
        Assert.That(result.Entries[1].IsDirectory).Is.True();
        Assert.That(result.Entries[2].Name).Is.EqualTo("a.dll");
        Assert.That(result.Entries[2].IsDirectory).Is.False();
        Assert.That(result.Entries[3].Name).Is.EqualTo("b.txt");
        Assert.That(result.Entries[3].IsDirectory).Is.False();
    }

    [Test]
    public static void ListInDirectoriesModeOmitsFilesEntirely()
    {
        var root = CreateFixtureTree();

        var result = FileBrowserService.List(root, FileBrowserMode.Directories, null);

        Assert.ThatCollection(result.Entries).Has.Count(2);
        Assert.That(result.Entries.All(entry => entry.IsDirectory)).Is.True();
    }

    [Test]
    public static void ListFiltersFilesByExtensionButKeepsEveryDirectory()
    {
        var root = CreateFixtureTree();

        var result = FileBrowserService.List(root, FileBrowserMode.Files, ".dll");

        Assert.ThatCollection(result.Entries).Has.Count(3);
        Assert
            .That(result.Entries.Count(entry => !entry.IsDirectory && entry.Name == "a.dll"))
            .Is.EqualTo(1);
        Assert.That(result.Entries.Any(entry => entry.Name == "b.txt")).Is.False();
    }

    [Test]
    public static void ListReportsSizeAndLastWriteTimeForFilesButNotDirectories()
    {
        var root = CreateFixtureTree();

        var result = FileBrowserService.List(root, FileBrowserMode.Files, null);

        var file = result.Entries.First(entry => entry.Name == "a.dll");
        Assert.That(file.Size!.Value).Is.GreaterThan(0L);
        Assert.That(string.IsNullOrEmpty(file.LastWriteTime)).Is.False();

        var dir = result.Entries.First(entry => entry.Name == "Alpha");
        Assert.That(dir.Size is null).Is.True();
        Assert.That(dir.LastWriteTime is null).Is.True();
    }

    [Test]
    public static void ListSetsParentPathToTheContainingDirectory()
    {
        var root = CreateFixtureTree();

        var result = FileBrowserService.List(root, FileBrowserMode.Files, null);

        Assert.That(result.ParentPath).Is.EqualTo(Directory.GetParent(root).FullName);
    }

    // Simulates browsing into a directory this mod's process lacks permission to read — a
    // permission-denied entry (or one that vanished mid-session) must degrade to an empty listing
    // rather than throwing and failing the whole request. chmod is Linux-specific, matching this
    // project's dev/test machine.
    [Test]
    public static void ListToleratesADirectoryItCannotRead()
    {
        var root = CreateFixtureTree();
        var locked = Path.Combine(root, "Locked");
        _ = Directory.CreateDirectory(locked);
        RunChmod("000", locked);

        try
        {
            var result = FileBrowserService.List(locked, FileBrowserMode.Files, null);

            Assert.That(result.CurrentPath).Is.EqualTo(Path.GetFullPath(locked));
            Assert.ThatCollection(result.Entries).Is.Empty();
        }
        finally
        {
            RunChmod("755", locked);
        }
    }

    [Test]
    public static void ListThrowsForAPathThatDoesNotExist()
    {
        var root = CreateFixtureTree();
        var missing = Path.Combine(root, "DoesNotExist");

        Assert
            .ThatFunc(() => FileBrowserService.List(missing, FileBrowserMode.Files, null))
            .Throw();
    }

    // Typing (or reopening) a full file path — e.g. an assembly path already picked in a prior
    // session — lists that file's containing directory instead of rejecting it as "not a
    // directory".
    [Test]
    public static void ListGivenAFilePathListsItsContainingDirectory()
    {
        var root = CreateFixtureTree();
        var file = Path.Combine(root, "a.dll");

        var result = FileBrowserService.List(file, FileBrowserMode.Files, null);

        Assert.That(result.CurrentPath).Is.EqualTo(root);
        Assert.That(result.Entries.Any(entry => entry.Name == "a.dll")).Is.True();
    }

    [Test]
    public static void ListIncludesAModsShortcutPointingAtTheModsFolder()
    {
        var result = FileBrowserService.List(null, FileBrowserMode.Files, null);

        var shortcut = result.Shortcuts.First(entry => entry.Kind == "Mods");
        Assert.That(shortcut.Path).Is.EqualTo(GenFilePaths.ModsFolderPath);
    }

    [Test]
    public static void ListOnlyIncludesShortcutsForFoldersThatActuallyExist()
    {
        var result = FileBrowserService.List(null, FileBrowserMode.Files, null);

        Assert.That(result.Shortcuts.All(entry => Directory.Exists(entry.Path))).Is.True();
    }

    [Test]
    public static void ListIncludesTheSameShortcutsRegardlessOfCurrentPath()
    {
        var root = CreateFixtureTree();

        var atRoot = FileBrowserService.List(null, FileBrowserMode.Files, null);
        var atSubdir = FileBrowserService.List(root, FileBrowserMode.Files, null);

        Assert.ThatCollection(atSubdir.Shortcuts).Has.Count(atRoot.Shortcuts.Count);
        foreach (var shortcut in atRoot.Shortcuts)
        {
            Assert.That(atSubdir.Shortcuts.Any(entry => entry.Kind == shortcut.Kind)).Is.True();
        }
    }

    private static void RunChmod(string mode, string path)
    {
        var startInfo = new ProcessStartInfo("chmod") { UseShellExecute = false };
        startInfo.ArgumentList.Add(mode);
        startInfo.ArgumentList.Add(path);
        using var process = Process.Start(startInfo);
        process.WaitForExit();
    }
}
