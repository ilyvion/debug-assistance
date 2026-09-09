using Mono.Cecil;
using MonoMod.Utils;

namespace DebugAssistance.Decompilation;

// Builds a small in-memory single-method assembly around an already-built method body (see
// PatchedMethodBuilder) so ICSharpCode.Decompiler — which only reads from an on-disk-shaped PEFile
// — can decompile it; there is no on-disk assembly for a DynamicMethod to read from directly. The
// Cecil-serialization step (GenerateCecilMethod) can't reuse a fresh MethodCopier pass to read the
// method's body, since that only works against a real reflected method; PatchedMethodBuilder's
// DynamicMethodDefinition already holds the merged method's IL directly
// (MethodBase.GetMethodBody() throws InvalidOperationException for a DynamicMethod on this
// runtime, so a second MethodCopier pass over it isn't an option here). Mono.Cecil and
// MonoMod.Utils ship merged inside 0Harmony.dll itself, so referencing them here needs no separate
// package — just the whole-assembly Krafs.Publicizer entry on 0Harmony in DebugAssistance.csproj
// that also unlocks Harmony's own internal patch-building types.
internal static class PatchedMethodAssemblyWriter
{
    internal const string DummyType = "DebugAssistanceDecompiledPatch";
    internal const string DummyDll = "decomp.dll";

    internal static void WriteAssembly(Stream stream, DynamicMethodDefinition dmd)
    {
        using var module = ModuleDefinition.CreateModule(
            dmd.GetDumpName("Cecil"),
            new ModuleParameters
            {
                Kind = ModuleKind.Dll,
                ReflectionImporterProvider = MMReflectionImporter.ProviderNoDefault,
            }
        );

        var typeDef = new TypeDefinition(
            "",
            DummyType,
            Mono.Cecil.TypeAttributes.Public
                | Mono.Cecil.TypeAttributes.Abstract
                | Mono.Cecil.TypeAttributes.Sealed
                | Mono.Cecil.TypeAttributes.Class
        )
        {
            BaseType = module.TypeSystem.Object,
        };
        module.Types.Add(typeDef);

        GenerateCecilMethod(dmd, typeDef);

        module.Write(stream);
    }

    // Ported from MonoMod.Utils.DMDCecilGenerator, edited to not load the assembly.
    private static void GenerateCecilMethod(DynamicMethodDefinition dmd, TypeDefinition typeDef)
    {
        var def = dmd.Definition;
        var module = typeDef.Module;

        IMetadataTokenProvider Relinker(IMetadataTokenProvider mtp, IGenericParameterProvider? ctx)
        {
            return module.ImportReference(mtp);
        }

        var clone = new MethodDefinition(
            dmd.Name ?? ("_" + def.Name.Replace('.', '_')),
            def.Attributes,
            module.TypeSystem.Void
        )
        {
            MethodReturnType = def.MethodReturnType,
            Attributes =
                Mono.Cecil.MethodAttributes.Public
                | Mono.Cecil.MethodAttributes.HideBySig
                | Mono.Cecil.MethodAttributes.Static,
            ImplAttributes =
                Mono.Cecil.MethodImplAttributes.IL | Mono.Cecil.MethodImplAttributes.Managed,
            DeclaringType = typeDef,
        };

        foreach (var param in def.Parameters)
        {
            clone.Parameters.Add(param.Clone().Relink(Relinker, clone));
        }

        clone.ReturnType = def.ReturnType.Relink(Relinker, clone);

        typeDef.Methods.Add(clone);

        clone.HasThis = def.HasThis;
        var body = clone.Body = def.Body.Clone(clone);

        foreach (var variable in clone.Body.Variables)
        {
            variable.VariableType = variable.VariableType.Relink(Relinker, clone);
        }

        foreach (var handler in clone.Body.ExceptionHandlers)
        {
            if (handler.CatchType != null)
            {
                handler.CatchType = handler.CatchType.Relink(Relinker, clone);
            }
        }

        for (var i = 0; i < body.Instructions.Count; i++)
        {
            var instruction = body.Instructions[i];
            var operand = instruction.Operand;

            if (operand is ParameterDefinition param)
            {
                operand = clone.Parameters[param.Index];
            }
            else if (operand is IMetadataTokenProvider mtp)
            {
                operand = mtp.Relink(Relinker, clone);
            }

            instruction.Operand = operand;
        }

        clone.HasThis = false;

        if (def.HasThis)
        {
            TypeReference type = def.DeclaringType;
            if (type.IsValueType)
            {
                type = new ByReferenceType(type);
            }

            clone.Parameters.Insert(
                0,
                new ParameterDefinition(
                    "<>_this",
                    Mono.Cecil.ParameterAttributes.None,
                    type.Relink(Relinker, clone)
                )
            );
        }
    }
}
