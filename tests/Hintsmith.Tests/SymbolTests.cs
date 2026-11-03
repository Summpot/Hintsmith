using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Hintsmith;
using Hintsmith.Tasks;

internal static class SymbolTests
{
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static MetadataReaderProvider Open(PEReader pe, string assembly)
    {
        var embedded = pe.ReadDebugDirectory().FirstOrDefault(d => d.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);
        if (embedded.Type == DebugDirectoryEntryType.EmbeddedPortablePdb) return pe.ReadEmbeddedPortablePdbDebugDirectoryData(embedded);
        Assert(pe.TryOpenAssociatedPortablePdb(assembly, path => File.Exists(path) ? File.OpenRead(path) : null, out var provider, out _), "Debugger cannot match DLL/PDB: " + assembly + " entries=" + string.Join(",", pe.ReadDebugDirectory().Select(e => e.Type)));
        return provider!;
    }
    public static void Verify(string before, string after)
    {
        using var sourcePe = new PEReader(File.OpenRead(before));
        using var targetPe = new PEReader(File.OpenRead(after));
        using var sourceProvider = Open(sourcePe, before);
        using var targetProvider = Open(targetPe, after);
        var source = sourceProvider.GetMetadataReader(); var target = targetProvider.GetMetadataReader();
        var sourceMetadata = sourcePe.GetMetadataReader(); var targetMetadata = targetPe.GetMetadataReader();
        Assert(!source.DebugMetadataHeader!.Id.SequenceEqual(target.DebugMetadataHeader!.Id), "PDB identity was not refreshed");
        Assert(source.Documents.Count == target.Documents.Count, "Documents lost");
        foreach (var h in source.Documents)
        {
            var a = source.GetDocument(h); var b = target.GetDocument(h);
            Assert(source.GetString(a.Name) == target.GetString(b.Name), "Document path changed");
            Assert(source.GetBlobBytes(a.Hash).SequenceEqual(target.GetBlobBytes(b.Hash)), "Document checksum changed");
        }
        int mappedMethods = 0, hidden = 0;
        foreach (var h in source.MethodDebugInformation)
        {
            var a = source.GetMethodDebugInformation(h); var b = target.GetMethodDebugInformation(h);
            var oldPoints = a.GetSequencePoints().ToArray(); var newPoints = b.GetSequencePoints().ToArray();
            var method = MetadataTokens.MethodDefinitionHandle(MetadataTokens.GetRowNumber(h));
            var originalDefinition = sourceMetadata.GetMethodDefinition(method);
            var definition = targetMetadata.GetMethodDefinition(method);
            Assert(sourceMetadata.GetString(originalDefinition.Name) == targetMetadata.GetString(definition.Name), "Method token changed");
            if (definition.RelativeVirtualAddress == 0) continue;
            var body = targetPe.GetMethodBody(definition.RelativeVirtualAddress);
            if (!b.SequencePointsBlob.IsNil)
                Assert(target.GetBlobReader(b.SequencePointsBlob).ReadCompressedInteger() == MetadataTokens.GetRowNumber(body.LocalSignature), "PDB local signature token is stale");
            foreach (var p in oldPoints.Where(p => !p.IsHidden))
                Assert(newPoints.Any(q => q.Document == p.Document && q.StartLine == p.StartLine && q.StartColumn == p.StartColumn && q.EndLine == p.EndLine && q.EndColumn == p.EndColumn), "Original source range lost");
            Assert(newPoints.Select(p => p.Offset).Distinct().Count() == newPoints.Length, "Duplicate sequence point offsets");
            Assert(newPoints.All(p => p.Offset >= 0 && p.Offset < body.GetILBytes()!.Length), "Sequence point outside IL");
            if (!oldPoints.Select(p => p.Offset).SequenceEqual(newPoints.Select(p => p.Offset))) mappedMethods++;
            hidden += newPoints.Count(p => p.IsHidden) - oldPoints.Count(p => p.IsHidden);
        }
        Assert(mappedMethods > 0 && hidden > 0, "No remapped methods or hidden hook points");
        foreach (var h in target.LocalScopes)
        {
            var scope = target.GetLocalScope(h);
            var body = targetPe.GetMethodBody(targetMetadata.GetMethodDefinition(scope.Method).RelativeVirtualAddress);
            Assert(scope.StartOffset >= 0 && scope.EndOffset <= body.GetILBytes()!.Length, "Local scope outside IL");
        }
        Assert(source.LocalVariables.Count == target.LocalVariables.Count, "Original local variables lost");
        Assert(source.LocalConstants.Count == target.LocalConstants.Count, "Local constants lost");
        foreach (var h in source.LocalVariables)
        {
            var a = source.GetLocalVariable(h); var b = target.GetLocalVariable(h);
            Assert(a.Index == b.Index && a.Attributes == b.Attributes && source.GetString(a.Name) == target.GetString(b.Name), "Local variable identity changed");
        }
        foreach (var h in source.LocalConstants)
        {
            var a = source.GetLocalConstant(h); var b = target.GetLocalConstant(h);
            Assert(source.GetString(a.Name) == target.GetString(b.Name) && source.GetBlobBytes(a.Signature).SequenceEqual(target.GetBlobBytes(b.Signature)), "Local constant changed");
        }
        foreach (var h in source.ImportScopes)
        {
            var a = source.GetImportScope(h); var b = target.GetImportScope(h);
            Assert(a.Parent == b.Parent, "Import parent changed");
            Assert(a.GetImports().Select(i => ImportShape(source, i)).SequenceEqual(b.GetImports().Select(i => ImportShape(target, i))), "Import aliases/namespaces corrupted");
        }
        foreach (var h in source.CustomDebugInformation)
        {
            var a = source.GetCustomDebugInformation(h);
            if (a.Parent.Kind is not (HandleKind.ModuleDefinition or HandleKind.Document)) continue;
            Assert(target.GetCustomDebugInformation(a.Parent).Any(x => {
                var b = target.GetCustomDebugInformation(x);
                return source.GetGuid(a.Kind) == target.GetGuid(b.Kind) && source.GetBlobBytes(a.Value).SequenceEqual(target.GetBlobBytes(b.Value));
            }), "SourceLink/embedded source/custom module data lost");
        }
        Console.WriteLine($"PASS symbols {before}: {mappedMethods} remapped methods, {hidden} hidden points, pairing, scopes, locals and source metadata");
    }
    public static void Run(string[] inputs)
    {
        var runtime = typeof(Hinting).Assembly.Location;
        foreach (var source in inputs)
        {
            var directory = Path.Combine(Path.GetTempPath(), "Hintsmith.SymbolTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var output = Path.Combine(directory, "Avalonia.Skia.dll");
                HintingWeaver.Rewrite(source, runtime, output);
                Verify(source, output);
                var stripped = Path.Combine(directory, "stripped.dll");
                HintingWeaver.Rewrite(source, runtime, stripped, SymbolMode.Strip);
                using (var pe = new PEReader(File.OpenRead(stripped)))
                    Assert(!pe.ReadDebugDirectory().Any(e => e.Type is DebugDirectoryEntryType.CodeView or DebugDirectoryEntryType.EmbeddedPortablePdb or DebugDirectoryEntryType.PdbChecksum), "Strip kept symbol entries");
                Assert(!File.Exists(Path.ChangeExtension(stripped, ".pdb")), "Strip emitted a PDB");
                using var sourcePe = new PEReader(File.OpenRead(source));
                if (!sourcePe.ReadDebugDirectory().Any(e => e.Type == DebugDirectoryEntryType.EmbeddedPortablePdb))
                {
                    var copy = Path.Combine(directory, "input.dll");
                    File.Copy(source, copy);
                    File.Copy(Path.ChangeExtension(output, ".pdb"), Path.ChangeExtension(copy, ".pdb"));
                    bool rejected = false;
                    try { HintingWeaver.Rewrite(copy, runtime, Path.Combine(directory, "bad.dll")); }
                    catch (InvalidOperationException ex) { rejected = ex.Message.Contains("PDB ID"); }
                    Assert(rejected && !File.Exists(Path.Combine(directory, "bad.dll")), "Mismatched PDB accepted");
                    var damaged = File.ReadAllBytes(Path.ChangeExtension(source, ".pdb"));
                    damaged[damaged.Length - 1] ^= 1;
                    File.WriteAllBytes(Path.ChangeExtension(copy, ".pdb"), damaged);
                    rejected = false;
                    try { HintingWeaver.Rewrite(copy, runtime, Path.Combine(directory, "bad.dll")); }
                    catch (InvalidOperationException ex) { rejected = ex.Message.Contains("checksum"); }
                    Assert(rejected, "PDB with matching ID but damaged checksum accepted");
                    File.WriteAllText(Path.ChangeExtension(copy, ".pdb"), "Microsoft C/C++ MSF unsupported symbol format");
                    rejected = false;
                    try { HintingWeaver.Rewrite(copy, runtime, Path.Combine(directory, "bad.dll")); }
                    catch (InvalidOperationException ex) { rejected = ex.Message.Contains("HintsmithSymbols=Strip"); }
                    Assert(rejected, "Unsupported PDB did not report explicit stripping option");
                    HintingWeaver.Rewrite(copy, runtime, Path.Combine(directory, "opt-out.dll"), SymbolMode.Strip);
                    File.Delete(Path.ChangeExtension(copy, ".pdb"));
                    HintingWeaver.Rewrite(copy, runtime, Path.Combine(directory, "no-symbols.dll"));
                    Assert(!File.Exists(Path.Combine(directory, "no-symbols.pdb")), "Missing symbols should not fabricate a PDB");
                }
            }
            finally { Directory.Delete(directory, true); }
        }
    }

    private static string ImportShape(MetadataReader reader, ImportDefinition import)
    {
        string Blob(BlobHandle h) => Convert.ToBase64String(reader.GetBlobBytes(h));
        return import.Kind + ":" + (import.Kind switch {
            ImportDefinitionKind.ImportNamespace => Blob(import.TargetNamespace),
            ImportDefinitionKind.ImportAssemblyNamespace => import.TargetAssembly + Blob(import.TargetNamespace),
            ImportDefinitionKind.ImportType => MetadataTokens.GetToken(import.TargetType).ToString(),
            ImportDefinitionKind.ImportXmlNamespace or ImportDefinitionKind.AliasNamespace => Blob(import.Alias) + Blob(import.TargetNamespace),
            ImportDefinitionKind.ImportAssemblyReferenceAlias => Blob(import.Alias),
            ImportDefinitionKind.AliasAssemblyReference => Blob(import.Alias) + MetadataTokens.GetRowNumber(import.TargetAssembly),
            ImportDefinitionKind.AliasAssemblyNamespace => Blob(import.Alias) + MetadataTokens.GetRowNumber(import.TargetAssembly) + Blob(import.TargetNamespace),
            ImportDefinitionKind.AliasType => Blob(import.Alias) + MetadataTokens.GetToken(import.TargetType),
            _ => throw new Exception("Unknown import") });
    }
}
