using System.Collections.Immutable;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using AsmResolver;
using ModuleDefinition = AsmResolver.DotNet.ModuleDefinition;
using AsmResolver.PE.Debug;

namespace Hintsmith.Tasks;

public enum SymbolMode { Preserve, Strip }

internal sealed class PortableSymbols : IDisposable
{
    private readonly MetadataReaderProvider _provider;
    private readonly MetadataReader _reader;
    private readonly bool _embedded;
    private PortableSymbols(byte[] bytes, bool embedded)
    {
        _provider = MetadataReaderProvider.FromPortablePdbImage(ImmutableArray.Create(bytes));
        try { _reader = _provider.GetMetadataReader(); }
        catch { _provider.Dispose(); throw; }
        _embedded = embedded;
    }

    public static PortableSymbols? Read(string assembly, string? symbolPath, SymbolMode mode)
    {
        try { return ReadCore(assembly, symbolPath, mode); }
        catch (Exception ex) when (!ex.Message.StartsWith("Cannot preserve symbols:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Cannot preserve symbols: " + ex.Message + " Supply a matching Portable PDB or explicitly set HintsmithSymbols=Strip.", ex);
        }
    }

    private static PortableSymbols? ReadCore(string assembly, string? symbolPath, SymbolMode mode)
    {
        if (mode == SymbolMode.Strip) return null;
        using var pe = new PEReader(File.OpenRead(assembly));
        var entries = pe.ReadDebugDirectory();
        var embedded = entries.Where(e => e.Type == DebugDirectoryEntryType.EmbeddedPortablePdb).ToArray();
        byte[]? bytes = null;
        if (embedded.Length > 1) throw new NotSupportedException("Multiple embedded PDBs are unsupported.");
        if (embedded.Length == 1)
        {
            // Let SRM validate the embedded header/decompression before extracting it.
            using var validation = pe.ReadEmbeddedPortablePdbDebugDirectoryData(embedded[0]);
            var data = pe.GetSectionData(embedded[0].DataRelativeVirtualAddress).GetContent(0, embedded[0].DataSize).ToArray();
            using var compressed = new MemoryStream(data, 8, data.Length - 8);
            using var deflate = new DeflateStream(compressed, CompressionMode.Decompress);
            using var expanded = new MemoryStream();
            deflate.CopyTo(expanded);
            bytes = expanded.ToArray();
        }
        else
        {
            var path = symbolPath ?? Path.ChangeExtension(assembly, ".pdb");
            if (File.Exists(path)) bytes = File.ReadAllBytes(path);
            else if (symbolPath != null) throw new FileNotFoundException("Explicit PDB not found.", path);
        }
        if (bytes == null) return null;
        PortableSymbols? result = null;
        try
        {
            result = new PortableSymbols(bytes, embedded.Length != 0);
            var id = new BlobContentId(result._reader.DebugMetadataHeader!.Id);
            var codeViews = entries.Where(e => e.Type == DebugDirectoryEntryType.CodeView).ToArray();
            if (codeViews.Length == 0 && embedded.Length == 0)
                throw new BadImageFormatException("External PDB has no DLL CodeView identity to match.");
            foreach (var entry in codeViews)
            {
                var cv = pe.ReadCodeViewDebugDirectoryData(entry);
                if (cv.Guid != id.Guid || entry.Stamp != id.Stamp || cv.Age != 1)
                    throw new BadImageFormatException("PDB ID does not match the input DLL.");
            }
            foreach (var entry in entries.Where(e => e.Type == DebugDirectoryEntryType.PdbChecksum))
            {
                var checksum = pe.ReadPdbChecksumDebugDirectoryData(entry);
                using HashAlgorithm algorithm = checksum.AlgorithmName switch {
                    "SHA256" => SHA256.Create(), "SHA1" => SHA1.Create(),
                    "SHA384" => SHA384.Create(), "SHA512" => SHA512.Create(),
                    _ => throw new NotSupportedException("Unsupported PDB checksum algorithm.") };
                var zeroed = (byte[])bytes.Clone();
                Array.Clear(zeroed, result._reader.DebugMetadataHeader.IdStartOffset, 20);
                if (!algorithm.ComputeHash(zeroed).SequenceEqual(checksum.Checksum))
                    throw new BadImageFormatException("PDB checksum does not match the input DLL.");
            }
            return result;
        }
        catch (Exception ex)
        {
            result?.Dispose();
            throw new InvalidOperationException("Cannot preserve symbols: " + ex.Message + " Supply a matching Portable PDB or explicitly set HintsmithSymbols=Strip.", ex);
        }
    }

    public static void RemoveStaleEntries(ModuleDefinition module)
    {
        // Preserve unrelated PE debug data. Repro is removed because this writer
        // does not claim a fully reproducible PE image (it assigns a new MVID).
        foreach (var entry in module.DebugData.Where(e => (uint?)e.Contents?.Type is 2 or 16 or 17 or 19).ToArray())
            module.DebugData.Remove(entry);
    }

    public byte[] Rewrite(ModuleDefinition module, PEReader rewrittenPe, Dictionary<int, MethodOffsetMap> maps, string output)
    {
        var reader = _reader;
        var metadata = rewrittenPe.GetMetadataReader();
        var builder = new MetadataBuilder();
        BlobHandle Blob(BlobHandle h) => h.IsNil ? default : builder.GetOrAddBlob(reader.GetBlobBytes(h));
        StringHandle String(StringHandle h) => h.IsNil ? default : builder.GetOrAddString(reader.GetString(h));
        GuidHandle GuidValue(GuidHandle h) => h.IsNil ? default : builder.GetOrAddGuid(reader.GetGuid(h));

        foreach (var h in reader.Documents)
        {
            var d = reader.GetDocument(h);
            builder.AddDocument(builder.GetOrAddDocumentName(reader.GetString(d.Name)), GuidValue(d.HashAlgorithm), Blob(d.Hash), GuidValue(d.Language));
        }
        foreach (var h in reader.ImportScopes)
        {
            var scope = reader.GetImportScope(h);
            var imports = new BlobBuilder();
            foreach (var import in scope.GetImports())
            {
                imports.WriteCompressedInteger((int)import.Kind);
                void Alias() => imports.WriteCompressedInteger(MetadataTokens.GetHeapOffset(Blob(import.Alias)));
                void Namespace() => imports.WriteCompressedInteger(MetadataTokens.GetHeapOffset(Blob(import.TargetNamespace)));
                void Assembly() => imports.WriteCompressedInteger(MetadataTokens.GetRowNumber(import.TargetAssembly));
                void Type() => imports.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(import.TargetType));
                switch (import.Kind)
                {
                    case ImportDefinitionKind.ImportNamespace: Namespace(); break;
                    case ImportDefinitionKind.ImportAssemblyNamespace: Assembly(); Namespace(); break;
                    case ImportDefinitionKind.ImportType: Type(); break;
                    case ImportDefinitionKind.ImportXmlNamespace:
                    case ImportDefinitionKind.AliasNamespace: Alias(); Namespace(); break;
                    case ImportDefinitionKind.ImportAssemblyReferenceAlias: Alias(); break;
                    case ImportDefinitionKind.AliasAssemblyReference: Alias(); Assembly(); break;
                    case ImportDefinitionKind.AliasAssemblyNamespace: Alias(); Assembly(); Namespace(); break;
                    case ImportDefinitionKind.AliasType: Alias(); Type(); break;
                    default: throw new NotSupportedException("Unsupported PDB import kind.");
                }
            }
            builder.AddImportScope(scope.Parent, builder.GetOrAddBlob(imports));
        }
        foreach (var h in reader.LocalVariables)
        {
            var v = reader.GetLocalVariable(h);
            builder.AddLocalVariable(v.Attributes, v.Index, String(v.Name));
        }
        foreach (var h in reader.LocalConstants)
        {
            var c = reader.GetLocalConstant(h);
            builder.AddLocalConstant(String(c.Name), Blob(c.Signature));
        }
        // List pointers for empty scopes must point to the next scope's first row,
        // not nil. Reconstruct them backwards, keeping all table RIDs unchanged.
        var scopes = reader.LocalScopes.ToArray();
        var variableStarts = new int[scopes.Length];
        var constantStarts = new int[scopes.Length];
        int variable = reader.LocalVariables.Count + 1, constant = reader.LocalConstants.Count + 1;
        for (int i = scopes.Length - 1; i >= 0; i--)
        {
            var scope = reader.GetLocalScope(scopes[i]);
            if (scope.GetLocalVariables().Any()) variable = MetadataTokens.GetRowNumber(scope.GetLocalVariables().First());
            if (scope.GetLocalConstants().Any()) constant = MetadataTokens.GetRowNumber(scope.GetLocalConstants().First());
            variableStarts[i] = variable; constantStarts[i] = constant;
        }
        for (int i = 0; i < scopes.Length; i++)
        {
            var scope = reader.GetLocalScope(scopes[i]);
            int start = scope.StartOffset, end = scope.EndOffset;
            if (maps.TryGetValue(MetadataTokens.GetRowNumber(scope.Method), out var map))
            { start = map.Boundary(start); end = map.Boundary(end); }
            builder.AddLocalScope(scope.Method, scope.ImportScope, MetadataTokens.LocalVariableHandle(variableStarts[i]),
                MetadataTokens.LocalConstantHandle(constantStarts[i]), start, end - start);
        }
        foreach (var h in reader.MethodDebugInformation)
        {
            var info = reader.GetMethodDebugInformation(h);
            int rid = MetadataTokens.GetRowNumber(h);
            var method = MetadataTokens.MethodDefinitionHandle(rid);
            var definition = metadata.GetMethodDefinition(method);
            var localSignature = definition.RelativeVirtualAddress == 0 ? default : rewrittenPe.GetMethodBody(definition.RelativeVirtualAddress).LocalSignature;
            maps.TryGetValue(rid, out var map);
            builder.AddMethodDebugInformation(info.Document, EncodePoints(builder, info, map, localSignature));
            var kickoff = info.GetStateMachineKickoffMethod();
            if (!kickoff.IsNil) builder.AddStateMachineMethod(method, kickoff);
        }
        foreach (var h in reader.CustomDebugInformation)
        {
            var info = reader.GetCustomDebugInformation(h);
            var kind = reader.GetGuid(info.Kind);
            var bytes = RewriteCustom(info, kind, maps);
            if (bytes != null) builder.AddCustomDebugInformation(info.Parent, builder.GetOrAddGuid(kind), builder.GetOrAddBlob(bytes));
        }
        var rows = Enumerable.Range(0, MetadataTokens.TableCount).Select(i => i < 48 ? metadata.GetTableRowCount((TableIndex)i) : 0).ToImmutableArray();
        var pdb = new BlobBuilder();
        byte[]? checksumBytes = null;
        var pdbBuilder = new PortablePdbBuilder(builder, rows, reader.DebugMetadataHeader!.EntryPoint, blobs => {
            using var data = new MemoryStream();
            foreach (var blob in blobs) { var segment = blob.GetBytes(); data.Write(segment.Array!, segment.Offset, segment.Count); }
            using var sha = SHA256.Create();
            checksumBytes = sha.ComputeHash(data.ToArray());
            return BlobContentId.FromHash(checksumBytes);
        });
        var id = pdbBuilder.Serialize(pdb);
        module.DebugData.Add(new DebugDataEntry(new RsdsDataSegment { Guid = id.Guid, Age = 1, Path = Path.GetFileName(Path.ChangeExtension(output, ".pdb")) })
            { MajorVersion = 0x100, MinorVersion = 0x504d, TimeDateStamp = id.Stamp });
        module.DebugData.Add(new DebugDataEntry(new CustomDebugDataSegment((DebugDataType)19,
            new DataSegment(System.Text.Encoding.UTF8.GetBytes("SHA256\0").Concat(checksumBytes!).ToArray()))) { MajorVersion = 1 });
        var result = pdb.ToArray();
        if (_embedded)
        {
            using var compressed = new MemoryStream();
            using (var writer = new BinaryWriter(compressed, System.Text.Encoding.UTF8, true)) { writer.Write(0x4244504d); writer.Write(result.Length); }
            using (var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, true)) deflate.Write(result, 0, result.Length);
            module.DebugData.Add(new DebugDataEntry(new CustomDebugDataSegment((DebugDataType)17, new DataSegment(compressed.ToArray()))) { MajorVersion = 0x100, MinorVersion = 0x100 });
        }
        return result;
    }

    public bool IsEmbedded => _embedded;

    private sealed class Point
    {
        public int Offset, Line, Column, EndLine, EndColumn;
        public DocumentHandle Document;
        public bool Hidden => Line == 0xfeefee;
        public Point At(int offset) => new() { Offset = offset, Line = Line, Column = Column, EndLine = EndLine, EndColumn = EndColumn, Document = Document };
        public static Point Hide(int offset, DocumentHandle document) => new() { Offset = offset, Line = 0xfeefee, EndLine = 0xfeefee, Document = document };
    }

    private static BlobHandle EncodePoints(MetadataBuilder builder, MethodDebugInformation info, MethodOffsetMap? map, StandaloneSignatureHandle localSignature)
    {
        if (info.SequencePointsBlob.IsNil) return default;
        var original = info.GetSequencePoints().Select(p => new Point { Offset = p.Offset, Line = p.StartLine, Column = p.StartColumn,
            EndLine = p.EndLine, EndColumn = p.EndColumn, Document = p.Document }).ToArray();
        if (original.Length == 0) return default;
        var points = new SortedDictionary<int, Point>();
        foreach (var p in original)
        {
            var offset = map == null ? p.Offset : map.SourceOffsets.TryGetValue(p.Offset, out var mapped) ? mapped :
                throw new NotSupportedException("Sequence point is not at an original instruction boundary.");
            points.Add(offset, p.At(offset));
        }
        if (map != null)
        foreach (var insertion in map.Insertions)
        {
            var active = original.LastOrDefault(p => p.Offset <= (insertion.NextOldOffset ?? int.MaxValue));
            points[insertion.Start] = Point.Hide(insertion.Start, active?.Document ?? original[0].Document);
            if (insertion.NextOldOffset.HasValue && active != null && !points.ContainsKey(insertion.End))
                points[insertion.End] = active.At(insertion.End);
        }
        var data = new BlobBuilder();
        data.WriteCompressedInteger(MetadataTokens.GetRowNumber(localSignature));
        var document = info.Document.IsNil ? points.First().Value.Document : info.Document;
        if (info.Document.IsNil) data.WriteCompressedInteger(MetadataTokens.GetRowNumber(document));
        int previousOffset = 0, previousLine = 0, previousColumn = 0;
        bool first = true, firstVisible = true;
        foreach (var p in points.Values)
        {
            if (p.Document != document)
            {
                data.WriteCompressedInteger(0); data.WriteCompressedInteger(MetadataTokens.GetRowNumber(p.Document)); document = p.Document;
            }
            data.WriteCompressedInteger(first ? p.Offset : p.Offset - previousOffset);
            previousOffset = p.Offset; first = false;
            if (p.Hidden) { data.WriteCompressedInteger(0); data.WriteCompressedInteger(0); continue; }
            int lines = p.EndLine - p.Line, columns = p.EndColumn - p.Column;
            data.WriteCompressedInteger(lines);
            if (lines == 0) data.WriteCompressedInteger(columns); else data.WriteCompressedSignedInteger(columns);
            if (firstVisible) { data.WriteCompressedInteger(p.Line); data.WriteCompressedInteger(p.Column); firstVisible = false; }
            else { data.WriteCompressedSignedInteger(p.Line - previousLine); data.WriteCompressedSignedInteger(p.Column - previousColumn); }
            previousLine = p.Line; previousColumn = p.Column;
        }
        return builder.GetOrAddBlob(data);
    }

    private static readonly Guid AsyncInfo = new("54FD2AC5-E925-401A-9C2A-F94F171072F8");
    private static readonly Guid HoistedScopes = new("6DA9A61E-F8C7-4874-BE62-68BC5630DF71");
    private static readonly HashSet<Guid> EncKinds = new() { new("755F52A8-91C5-45BE-B4B8-209571E552BD"), new("A643004C-0240-496F-A783-30D64F4979DE"), new("8B78CD68-2EDE-420B-980B-E15884B8AAA3") };
    private static readonly HashSet<Guid> SafeMethodKinds = new() { new("83C563C4-B4F3-47D5-B824-BA5441477EA8"), new("ED9FDF71-8879-4747-8ED3-FE5EDE3CE710") };

    private byte[]? RewriteCustom(CustomDebugInformation info, Guid kind, Dictionary<int, MethodOffsetMap> maps)
    {
        var bytes = _reader.GetBlobBytes(info.Value);
        if (info.Parent.Kind != HandleKind.MethodDefinition) return bytes;
        maps.TryGetValue(MetadataTokens.GetRowNumber((MethodDefinitionHandle)info.Parent), out var map);
        if (kind == AsyncInfo)
        {
            var reader = _reader.GetBlobReader(info.Value);
            var writer = new BlobBuilder();
            uint catchOffset = reader.ReadUInt32();
            writer.WriteUInt32(catchOffset == 0 || map == null ? catchOffset : checked((uint)map.Boundary((int)catchOffset - 1) + 1));
            while (reader.RemainingBytes != 0)
            {
                int yield = reader.ReadInt32(), resume = reader.ReadInt32(), method = reader.ReadCompressedInteger();
                writer.WriteInt32(map == null ? yield : map.Boundary(yield));
                writer.WriteInt32(maps.TryGetValue(method, out var resumeMap) ? resumeMap.Boundary(resume) : resume);
                writer.WriteCompressedInteger(method);
            }
            return writer.ToArray();
        }
        if (map == null || !map.Changed) return bytes;
        if (EncKinds.Contains(kind)) return null; // Hot Reload baselines cannot survive IL weaving.
        if (kind == HoistedScopes)
        {
            var reader = _reader.GetBlobReader(info.Value);
            var writer = new BlobBuilder();
            while (reader.RemainingBytes != 0)
            {
                int start = reader.ReadInt32(), length = reader.ReadInt32();
                if (start == 0 && length == 0) { writer.WriteInt32(0); writer.WriteInt32(0); }
                else { int mapped = map.Boundary(start); writer.WriteInt32(mapped); writer.WriteInt32(map.Boundary(start + length) - mapped); }
            }
            return writer.ToArray();
        }
        if (!SafeMethodKinds.Contains(kind)) throw new NotSupportedException($"Unknown custom debug information {kind} on a rewritten method. Use HintsmithSymbols=Strip to opt out.");
        return bytes;
    }

    public void Dispose() => _provider.Dispose();
}
