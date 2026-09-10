using DebugAssistance.HotPatch;
using RimTestRedux;

namespace DebugAssistance.Tests.HotPatch;

[TestSuite]
internal static class CSharpTypeFormatterTests
{
    private sealed class GenericFixture<T>
    {
#pragma warning disable CA1822 // Mark members as static -- deliberately an instance method
        public void DoTheThing() { }
#pragma warning restore CA1822
    }

    [Test]
    public static void FormatTypeRendersAClosedGenericTypeAsValidCSharp() =>
        Assert
            .That(CSharpTypeFormatter.FormatType(typeof(List<int>)))
            .Is.EqualTo("System.Collections.Generic.List<int>");

    [Test]
    public static void FormatTypeRendersANestedClosedGenericTypeAsValidCSharp() =>
        Assert
            .That(CSharpTypeFormatter.FormatType(typeof(GenericFixture<string>)))
            .Is.EqualTo(
                "DebugAssistance.Tests.HotPatch.CSharpTypeFormatterTests.GenericFixture<string>"
            );

    // This is the scenario fb408e1 fixed for ProjectScaffolder's own formatter (and the reason
    // DescribeMethod routes through this shared one too): Type.FullName on a closed generic type
    // renders CLR notation (backticks, bracketed assembly-qualified argument lists) rather than
    // anything resembling C#, which is exactly what ends up embedded in AI-prompt and active-patch
    // descriptions if DescribeMethod doesn't normalize it first.
    [Test]
    public static void DescribeMethodRendersAMethodOnAGenericDeclaringTypeAsValidCSharp()
    {
        var method = typeof(GenericFixture<string>).GetMethod(nameof(GenericFixture<>.DoTheThing));

        Assert
            .That(CSharpTypeFormatter.DescribeMethod(method))
            .Is.EqualTo(
                "DebugAssistance.Tests.HotPatch.CSharpTypeFormatterTests.GenericFixture<string>.DoTheThing"
            );
    }

    // Regression coverage for a live stack overflow: a captured frame on
    // List<T>.Enumerator.MoveNextRare (a struct nested in a generic collection type, reached
    // through a Harmony-patched, dynamically-generated method) drove FormatType into unbounded
    // recursion via its DeclaringType/generic-argument chain. This must terminate and produce
    // something, even if the exact rendering of the mismatched open/closed generic argument isn't
    // load-bearing.
    [Test]
    public static void FormatTypeTerminatesForANestedTypeOfAGenericCollection() =>
        Assert
            .That(CSharpTypeFormatter.FormatType(typeof(List<int>.Enumerator)).Length > 0)
            .Is.True();
}
