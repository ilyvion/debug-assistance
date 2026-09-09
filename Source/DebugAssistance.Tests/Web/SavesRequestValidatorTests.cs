using DebugAssistance.Web;
using RimTestRedux;

namespace DebugAssistance.Tests.Web;

[TestSuite]
internal static class SavesRequestValidatorTests
{
    [Test]
    public static void IsValidFileNameRejectsNull() =>
        Assert.That(SavesRequestValidator.IsValidFileName(null)).Is.False();

    [Test]
    public static void IsValidFileNameRejectsPathSeparators() =>
        Assert.That(SavesRequestValidator.IsValidFileName("in/valid")).Is.False();

    [Test]
    public static void IsValidFileNameAcceptsAnOrdinaryName() =>
        Assert.That(SavesRequestValidator.IsValidFileName("MyCapturedErrors")).Is.True();

    [Test]
    public static void ConflictsWithExistingSaveIsFalseWhenOverwriteIsRequested() =>
        Assert
            .That(
                SavesRequestValidator.ConflictsWithExistingSave(
                    overwrite: true,
                    existingFileExists: true
                )
            )
            .Is.False();

    [Test]
    public static void ConflictsWithExistingSaveIsFalseWhenNoFileExistsYet() =>
        Assert
            .That(
                SavesRequestValidator.ConflictsWithExistingSave(
                    overwrite: false,
                    existingFileExists: false
                )
            )
            .Is.False();

    [Test]
    public static void ConflictsWithExistingSaveIsTrueWhenNotOverwritingAndTheFileAlreadyExists() =>
        Assert
            .That(
                SavesRequestValidator.ConflictsWithExistingSave(
                    overwrite: false,
                    existingFileExists: true
                )
            )
            .Is.True();

    [Test]
    public static void IsValidLoadModeAcceptsMergeAndReplace()
    {
        Assert.That(SavesRequestValidator.IsValidLoadMode("merge")).Is.True();
        Assert.That(SavesRequestValidator.IsValidLoadMode("replace")).Is.True();
    }

    [Test]
    public static void IsValidLoadModeRejectsAnythingElse()
    {
        Assert.That(SavesRequestValidator.IsValidLoadMode(null)).Is.False();
        Assert.That(SavesRequestValidator.IsValidLoadMode("")).Is.False();
        Assert.That(SavesRequestValidator.IsValidLoadMode("Merge")).Is.False();
        Assert.That(SavesRequestValidator.IsValidLoadMode("append")).Is.False();
    }
}
