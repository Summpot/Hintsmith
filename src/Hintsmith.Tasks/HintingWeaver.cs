using AsmResolver.DotNet;
using AsmResolver.DotNet.Builder;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using static AsmResolver.PE.DotNet.Cil.CilOpCodes;

namespace Hintsmith.Tasks;

public static class HintingWeaver
{
    public const string Abi = "Hintsmith.HookV1";

    public static string Rewrite(string source, string runtime, string output, SymbolMode symbols = SymbolMode.Preserve, string? symbolPath = null)
    {
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output must differ from the original assembly path.", nameof(output));
        if (!Enum.IsDefined(typeof(SymbolMode), symbols)) throw new ArgumentOutOfRangeException(nameof(symbols));
        var outputPdb = Path.ChangeExtension(output, ".pdb");
        if (string.Equals(Path.GetFullPath(symbolPath ?? Path.ChangeExtension(source, ".pdb")), Path.GetFullPath(outputPdb), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output PDB must differ from the original PDB path.", nameof(output));
        using var pdb = PortableSymbols.Read(source, symbolPath, symbols);
        var module = ModuleDefinition.FromFile(source);
        if (module.Assembly?.Name != "Avalonia.Skia" || module.Assembly.Version.Major is not (11 or 12))
            throw new NotSupportedException("Expected an Avalonia.Skia 11.x or 12.x assembly.");
        if (module.GetImportedTypeReferences().Any(t => t.FullName == Abi))
            throw new InvalidOperationException("The input has already been woven. Use the original Avalonia.Skia assembly.");
        var replacements = new Dictionary<CilInstruction, CilInstruction>();
        var maps = module.GetAllTypes().SelectMany(t => t.Methods).Where(m => m.CilMethodBody != null)
            .ToDictionary(m => (int)m.MetadataToken.Rid, m => new MethodOffsetMap(m, replacements));
        var glyph = module.TopLevelTypes.Single(t => t.FullName == "Avalonia.Skia.GlyphRunImpl");
        var blob = glyph.Methods.Single(m => m.Name == "GetTextBlob");
        var cache = glyph.Fields.Single(f => f.Name == "_textBlobCache");
        var hooks = ModuleDefinition.FromFile(runtime).TopLevelTypes.Single(t => t.FullName == Abi);
        IMethodDescriptor Hook(string name) => module.DefaultImporter.ImportMethod(hooks.Methods.Single(m => m.Name == name));
        foreach (var m in glyph.Methods.Concat(glyph.NestedTypes.SelectMany(t => t.Methods)))
            m.CilMethodBody?.Instructions.ExpandMacros();

        string profile;
        if (blob.Signature!.ParameterTypes.Count == 2 && blob.Signature.ParameterTypes[0].FullName == "Avalonia.Media.TextOptions")
        {
            if (cache.Signature!.FieldType.FullName != "Avalonia.Skia.TwoLevelCache`2<Avalonia.Media.TextOptions, SkiaSharp.SKTextBlob>")
                throw new NotSupportedException("Unrecognized TextOptions cache structure.");
            profile = "TextOptions";
            PatchTextOptions(glyph, blob, Hook, replacements);
        }
        else if (blob.Signature.ParameterTypes.Count == 1 && blob.Signature.ParameterTypes[0].FullName == "Avalonia.Media.RenderOptions")
        {
            profile = cache.Signature!.FieldType is SzArrayTypeSignature ? "EdgingArray" : "EdgingDictionary";
            PatchLegacy(module, glyph, blob, cache, Hook, replacements);
        }
        else throw new NotSupportedException("Unrecognized GetTextBlob signature; no output was written.");

        foreach (var m in glyph.Methods.Concat(glyph.NestedTypes.SelectMany(t => t.Methods)))
            m.CilMethodBody?.Instructions.OptimizeMacros();

        // Keep public-key identity: Avalonia's signed friend assemblies require it.
        // Modern .NET does not validate strong-name signatures. Do not claim a valid
        // signature; PortableSymbols independently rebuilds matching debug information.
        module.IsStrongNameSigned = false;
        PortableSymbols.RemoveStaleEntries(module);
        module.Mvid = Guid.NewGuid();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        foreach (var map in maps.Values) map.Complete();
        byte[]? symbolBytes = null;
        if (pdb != null)
        {
            using var intermediate = new MemoryStream();
            module.Write(intermediate, new ManagedPEImageBuilder(MetadataBuilderFlags.PreserveAll));
            intermediate.Position = 0;
            using var pe = new System.Reflection.PortableExecutable.PEReader(intermediate);
            symbolBytes = pdb.Rewrite(module, pe, maps, output);
        }
        module.Write(output, new ManagedPEImageBuilder(MetadataBuilderFlags.PreserveAll));
        if (symbolBytes != null && !pdb!.IsEmbedded)
            File.WriteAllBytes(outputPdb, symbolBytes);
        else if (File.Exists(outputPdb)) File.Delete(outputPdb);
        return profile;
    }

    private static void PatchTextOptions(TypeDefinition glyph, MethodDefinition blob, Func<string, IMethodDescriptor> hook, Dictionary<CilInstruction, CilInstruction> replacements)
    {
        var font = glyph.Methods.Single(m => m.Name == "CreateFont" && m.Signature!.ParameterTypes[0].FullName == "Avalonia.Media.TextOptions");
        var members = glyph.Methods.Where(m => m.CilMethodBody != null)
            .SelectMany(m => m.CilMethodBody!.Instructions).Select(i => i.Operand).OfType<IMethodDescriptor>().ToArray();
        var getter = members.First(m => m.DeclaringType?.FullName == "Avalonia.Media.TextOptions" && m.Name == "get_TextHintingMode");
        var setter = members.First(m => m.DeclaringType?.FullName == "Avalonia.Media.TextOptions" && m.Name == "set_TextHintingMode");
        foreach (var method in new[] { blob, font })
        {
            var arg = method.Parameters[0];
            method.CilMethodBody!.Instructions.InsertRange(0, new[] {
                new CilInstruction(Ldarga, arg), new CilInstruction(Ldarga, arg),
                new CilInstruction(Call, getter), new CilInstruction(Call, hook("NormalizeTextHinting")),
                new CilInstruction(Call, setter)
            });
        }
        WrapSetter(replacements, font, "set_Hinting", new[] {
            new CilInstruction(Ldarga, font.Parameters[0]), new CilInstruction(Call, getter),
            new CilInstruction(Call, hook("TextHinting")) });
        WrapSetter(replacements, font, "set_ForceAutoHinting", new[] {
            new CilInstruction(Ldarga, font.Parameters[0]), new CilInstruction(Call, getter),
            new CilInstruction(Call, hook("ForceAutoHinting")) });
    }

    private static void PatchLegacy(ModuleDefinition module, TypeDefinition glyph, MethodDefinition blob,
        FieldDefinition cache, Func<string, IMethodDescriptor> hook, Dictionary<CilInstruction, CilInstruction> replacements)
    {
        var body = blob.CilMethodBody!;
        var mode = new CilLocalVariable(module.CorLibTypeFactory.Int32);
        body.LocalVariables.Add(mode);
        var stores = body.Instructions.Where(i => i.OpCode == Stfld && i.Operand is IFieldDescriptor f && f.Signature!.FieldType.FullName == "SkiaSharp.SKFontEdging").ToArray();
        FieldDefinition? captured = null;
        if (cache.Signature!.FieldType is SzArrayTypeSignature)
        {
            var edging = body.LocalVariables.Single(v => v.VariableType.FullName == "SkiaSharp.SKFontEdging");
            stores = body.Instructions.Where(i => i.OpCode == Stloc && ReferenceEquals(i.Operand, edging)).ToArray();
            var ctor = glyph.Methods.Single(m => m.IsConstructor && !m.IsStatic);
            var ci = ctor.CilMethodBody!.Instructions;
            var allocation = ci.Single(i => i.OpCode == Newarr && ((ITypeDefOrRef)i.Operand!).FullName == "SkiaSharp.SKTextBlob");
            var length = ci[ci.IndexOf(allocation) - 1];
            if (!length.IsLdcI4() || length.GetLdcI4Constant() != 3)
                throw new NotSupportedException("Unrecognized edging array allocation.");
            length.ReplaceWith(Ldc_I4, 28); // [16..27]: four hinting modes x three edgings.
        }
        else
        {
            if (!cache.Signature.FieldType.FullName.StartsWith("System.Collections.Concurrent.ConcurrentDictionary`2<SkiaSharp.SKFontEdging,"))
                throw new NotSupportedException("Unrecognized legacy text blob cache.");
            captured = stores.Select(i => (FieldDefinition)i.Operand!).Distinct().Single();
        }
        if (stores.Length < 1) throw new NotSupportedException("Cannot locate edging assignments.");
        foreach (var store in stores)
            Prepend(replacements, body.Instructions, store, new[] { new CilInstruction(Ldloc, mode), new CilInstruction(Call, hook("PackEdging")) });
        body.Instructions.InsertRange(0, new[] { new CilInstruction(Call, hook("SnapshotLegacy")), new CilInstruction(Stloc, mode) });

        var font = glyph.Methods.SingleOrDefault(m => m.Name == "CreateFont");
        if (font != null)
        {
            if (font.Signature!.ParameterTypes.Count != 1 || font.Signature.ParameterTypes[0].FullName != "SkiaSharp.SKFontEdging")
                throw new NotSupportedException("Unrecognized legacy CreateFont signature.");
            var token = new CilLocalVariable(module.CorLibTypeFactory.Int32);
            font.CilMethodBody!.LocalVariables.Add(token);
            font.CilMethodBody.Instructions.InsertRange(0, new[] {
                new CilInstruction(Ldarg, font.Parameters[0]), new CilInstruction(Call, hook("NormalizeEdging")),
                new CilInstruction(Dup), new CilInstruction(Stloc, token), new CilInstruction(Call, hook("DecodeEdging")),
                new CilInstruction(Starg, font.Parameters[0]) });
            WrapSetter(replacements, font, "set_Hinting", new[] { new CilInstruction(Ldloc, token), new CilInstruction(Call, hook("LegacyHinting")) });
        }
        else
        {
            // 11.0 creates/configures its shared SKFont inside the cache factory.
            if (captured == null) throw new NotSupportedException("Missing legacy closure.");
            var factory = captured.DeclaringType!.Methods.Single(m => m.CilMethodBody?.Instructions.Any(i => IsSkFontSetter(i, "set_Hinting")) == true);
            foreach (var load in factory.CilMethodBody!.Instructions.Where(i => i.OpCode == Ldfld && ReferenceEquals(i.Operand, captured)).ToArray())
                factory.CilMethodBody.Instructions.Insert(factory.CilMethodBody.Instructions.IndexOf(load) + 1, Call, hook("DecodeEdging"));
            WrapSetter(replacements, factory, "set_Hinting", new[] { new CilInstruction(Ldarg_0), new CilInstruction(Ldfld, captured), new CilInstruction(Call, hook("LegacyHinting")) });
        }
    }

    private static bool IsSkFontSetter(CilInstruction i, string name) =>
        (i.OpCode == Call || i.OpCode == Callvirt) && i.Operand is IMethodDescriptor m && m.Name == name && m.DeclaringType?.FullName == "SkiaSharp.SKFont";

    private static void WrapSetter(Dictionary<CilInstruction, CilInstruction> replacements, MethodDefinition method, string name, CilInstruction[] prefix)
    {
        var instructions = method.CilMethodBody!.Instructions;
        var setter = instructions.Single(i => IsSkFontSetter(i, name));
        Prepend(replacements, instructions, setter, prefix);
    }

    // Preserve the original instruction object: existing branches/EH boundaries must
    // enter the added code, rather than bypassing it or pointing at a removed object.
    private static void Prepend(Dictionary<CilInstruction, CilInstruction> replacements, CilInstructionCollection instructions, CilInstruction target, CilInstruction[] prefix)
    {
        var original = new CilInstruction(target.OpCode, target.Operand);
        replacements[target] = original;
        var index = instructions.IndexOf(target);
        target.OpCode = prefix[0].OpCode;
        target.Operand = prefix[0].Operand;
        instructions.InsertRange(index + 1, prefix.Skip(1).Concat(new[] { original }));
    }
}

