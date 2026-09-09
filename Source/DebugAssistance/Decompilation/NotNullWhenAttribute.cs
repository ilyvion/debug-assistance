namespace System.Diagnostics.CodeAnalysis;

// net481's own System.Diagnostics.CodeAnalysis.NotNullWhenAttribute (in mscorlib) is unusable here
// unqualified: DebugAssistance.csproj's whole-assembly `<Publicize Include="0Harmony" />` (see that
// file) also makes 0Harmony's own embedded copy of this attribute public, so the two referenced
// assemblies expose an identically-named type and any unqualified use is ambiguous (CS0433). A
// same-named type declared directly in this project's own source takes precedence over both
// imported ones (CS0436, suppressed via #pragma around its FrameDecompiler.cs use site) instead
// of conflicting with them.
[AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
internal sealed class NotNullWhenAttribute(bool returnValue) : Attribute
{
    public bool ReturnValue { get; } = returnValue;
}
