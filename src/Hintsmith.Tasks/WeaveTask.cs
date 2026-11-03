using System.Security.Cryptography;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Hintsmith.Tasks;

/// <summary>Replaces file items without changing NuGet's cache or assembly identity.</summary>
public sealed class WeaveTask : Microsoft.Build.Utilities.Task
{
    [Required] public ITaskItem[] InputFiles { get; set; } = Array.Empty<ITaskItem>();
    [Required] public string RuntimeAssembly { get; set; } = "";
    [Required] public string IntermediateDirectory { get; set; } = "";
    public string Symbols { get; set; } = "Preserve";
    public string PdbPath { get; set; } = "";
    [Output] public ITaskItem[] OutputFiles { get; set; } = Array.Empty<ITaskItem>();
    [Output] public ITaskItem[] WrittenFiles { get; set; } = Array.Empty<ITaskItem>();

    public override bool Execute()
    {
        try
        {
            if (!Enum.TryParse<SymbolMode>(Symbols, true, out var symbolMode) || !Enum.IsDefined(typeof(SymbolMode), symbolMode))
                throw new ArgumentException("HintsmithSymbols must be Preserve or Strip.");
            var targets = InputFiles.Where(i => Path.GetFileName(i.ItemSpec).Equals("Avalonia.Skia.dll", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (targets.Length == 0) { OutputFiles = InputFiles; return true; }
            if (!File.Exists(RuntimeAssembly)) throw new FileNotFoundException("Hintsmith runtime assembly was not resolved. Keep compile and runtime assets enabled on the application's Hintsmith PackageReference.", RuntimeAssembly);
            var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in targets)
            {
                var source = Path.GetFullPath(item.ItemSpec);
                var root = Path.GetFullPath(IntermediateDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                // A later publish phase may already contain our woven build item.
                if (source.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                var adjacent = Path.ChangeExtension(source, ".pdb");
                var symbolPath = !string.IsNullOrWhiteSpace(PdbPath) ? Path.GetFullPath(PdbPath) : File.Exists(adjacent) ? adjacent : InputFiles
                    .Where(i => Path.GetFileName(i.ItemSpec).Equals("Avalonia.Skia.pdb", StringComparison.OrdinalIgnoreCase))
                    .Select(i => i.ItemSpec).Distinct(StringComparer.OrdinalIgnoreCase).SingleOrDefault();
                var key = HashText(Hash(source, RuntimeAssembly, typeof(WeaveTask).Assembly.Location) + "|" + symbolMode + "|" +
                    (symbolMode == SymbolMode.Preserve && symbolPath != null ? Hash(symbolPath) : "no-external-symbols"));
                var directory = Path.Combine(root, key);
                var output = Path.Combine(directory, "Avalonia.Skia.dll");
                Directory.CreateDirectory(directory);
                // Different build nodes may meet at the same content-addressed output.
                // The lock is process-independent; an incomplete write is never reused.
                using (var gate = new Mutex(false, "Hintsmith_" + HashText(output)))
                {
                    try { gate.WaitOne(); } catch (AbandonedMutexException) { }
                    try
                    {
                        var outputPdb = Path.ChangeExtension(output, ".pdb");
                        var symbolMarker = Path.Combine(directory, "external-symbols");
                        if (!File.Exists(output) || (File.Exists(symbolMarker) && !File.Exists(outputPdb)))
                        {
                            var staging = Path.Combine(directory, Guid.NewGuid().ToString("N"));
                            Directory.CreateDirectory(staging);
                            var temporary = Path.Combine(staging, "Avalonia.Skia.dll");
                            try
                            {
                                var profile = HintingWeaver.Rewrite(source, RuntimeAssembly, temporary, symbolMode, symbolPath);
                                var temporaryPdb = Path.ChangeExtension(temporary, ".pdb");
                                if (File.Exists(temporaryPdb))
                                {
                                    File.Copy(temporaryPdb, outputPdb, true);
                                    File.WriteAllText(symbolMarker, "Portable PDB");
                                }
                                // DLL is the commit marker: it becomes visible only after its PDB.
                                if (File.Exists(output)) File.Delete(output);
                                File.Move(temporary, output);
                                Log.LogMessage(MessageImportance.High, "Hintsmith: {0} ({1})", source, profile);
                            }
                            finally { Directory.Delete(staging, true); }
                        }
                    }
                    finally { gate.ReleaseMutex(); }
                }
                replacements[source] = output;
            }

            var rewritten = InputFiles.Where(i => !Path.GetFileName(i.ItemSpec).Equals("Avalonia.Skia.pdb", StringComparison.OrdinalIgnoreCase))
                .Select(i =>
                {
                    if (!replacements.TryGetValue(Path.GetFullPath(i.ItemSpec), out var output)) return i;
                    var result = new TaskItem(i) { ItemSpec = output };
                    result.SetMetadata("PostprocessAssembly", "true");
                    // Copy task metadata may otherwise continue pointing at the original.
                    result.RemoveMetadata("OriginalItemSpec");
                    return (ITaskItem)result;
                }).ToList();
            foreach (var assembly in rewritten.Where(i => Path.GetFileName(i.ItemSpec).Equals("Avalonia.Skia.dll", StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                var pdb = Path.ChangeExtension(assembly.ItemSpec, ".pdb");
                if (!File.Exists(pdb) || symbolMode == SymbolMode.Strip) continue;
                var item = new TaskItem(assembly) { ItemSpec = pdb };
                foreach (var name in new[] { "RelativePath", "TargetPath", "DestinationSubPath" })
                    if (!string.IsNullOrEmpty(item.GetMetadata(name))) item.SetMetadata(name, Path.ChangeExtension(item.GetMetadata(name), ".pdb"));
                item.SetMetadata("PostprocessAssembly", "false");
                item.SetMetadata("AssetType", "symbols");
                rewritten.Add(item);
            }
            OutputFiles = rewritten
                // Publish can carry both build's woven item and a freshly resolved
                // package asset. They now identify the same output; crossgen accepts
                // exactly one item for each compilation entry.
                .GroupBy(i => Path.GetFullPath(i.ItemSpec) + "|" + i.GetMetadata("RelativePath") + "|" + i.GetMetadata("DestinationSubDirectory"), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First()).ToArray();
            WrittenFiles = replacements.Values.SelectMany(p => new[] { p, Path.ChangeExtension(p, ".pdb"), Path.Combine(Path.GetDirectoryName(p)!, "external-symbols") })
                .Where(File.Exists).Distinct().Select(p => (ITaskItem)new TaskItem(p)).ToArray();
            return true;
        }
        catch (Exception ex)
        {
            Log.LogError("Hintsmith: {0}", ex.Message);
            return false;
        }
    }

    private static string Hash(params string[] paths)
    {
        using var sha = SHA256.Create();
        var hashes = paths.Select(p => Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(p))));
        return HashText(string.Join("|", hashes));
    }

    private static string HashText(string text)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
    }
}
