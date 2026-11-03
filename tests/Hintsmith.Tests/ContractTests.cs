using System.Security.Cryptography;
using AsmResolver.DotNet;
using Hintsmith;
using Hintsmith.Tasks;

internal static class ContractTests
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}");
    }

    public static void Run(string[] args)
    {
        var runtime = typeof(Hinting).Assembly.Location;
        Require(Hinting.Current == HintingMode.Normal, "Default is not Normal");
        foreach (var mode in new[] { HintingMode.None, HintingMode.Slight, HintingMode.Normal, HintingMode.Full })
        {
            Hinting.SetHinting(mode);
            var text = HookV1.NormalizeTextHinting(2);
            var snapshot = HookV1.SnapshotLegacy();
            Hinting.RestoreAvaloniaDefaults();
            Require(HookV1.NormalizeTextHinting(text) == text, "Text snapshot is not idempotent");
            Require(HookV1.TextHinting(3, text) == (int)mode, "Text snapshot changed after policy update");
            Require(HookV1.ForceAutoHinting(false, text) == (mode == HintingMode.Slight), "Auto hinting mismatch");
            foreach (var edging in new[] { 0, 1, 2 })
            {
                var key = HookV1.PackEdging(edging, snapshot);
                Require(key >= 16 && key < 28, "Legacy key exceeds array capacity");
                Require(HookV1.DecodeEdging(key) == edging, "Edging was not preserved");
                Require(HookV1.LegacyHinting(3, key) == (int)mode, "Legacy snapshot changed");
                Require(HookV1.NormalizeEdging(key) == key, "Legacy snapshot is not idempotent");
            }
        }
        Hinting.RestoreAvaloniaDefaults();
        foreach (var (input, expected) in new[] { (0, 3), (1, 0), (2, 1), (3, 3) })
            Require(HookV1.TextHinting(3, HookV1.NormalizeTextHinting((byte)input)) == expected, "Original Avalonia policy changed");
        Throws<ArgumentOutOfRangeException>(() => Hinting.SetHinting((HintingMode)4));
        Throws<ArgumentOutOfRangeException>(() => Hinting.SetHinting((HintingMode)(-2)));

        if (args.Length == 2 && args[0] == "assert-woven")
        {
            Require(ModuleDefinition.FromFile(args[1]).GetImportedTypeReferences().Any(t => t.FullName == HintingWeaver.Abi), "Missing hook ABI");
            Console.WriteLine("PASS woven publish output");
            return;
        }

        foreach (var source in args)
        {
            var directory = Path.Combine(Path.GetTempPath(), "Hintsmith.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var output = Path.Combine(directory, "Avalonia.Skia.dll");
                var originalHash = SHA256.HashData(File.ReadAllBytes(source));
                var profile = HintingWeaver.Rewrite(source, runtime, output);
                var before = ModuleDefinition.FromFile(source);
                var after = ModuleDefinition.FromFile(output);
                Require(before.Assembly!.FullName == after.Assembly!.FullName, "Assembly identity changed");
                Require(before.Mvid != after.Mvid, "MVID should identify rewritten content");
                if (File.Exists(Path.ChangeExtension(output, ".pdb"))) SymbolTests.Verify(source, output);
                Require(!after.IsStrongNameSigned, "Invalid signature marked valid");
                Require(after.GetImportedTypeReferences().Any(t => t.FullName == HintingWeaver.Abi), "Missing hook reference");
                Require(originalHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))), "Source modified");
                Throws<InvalidOperationException>(() => HintingWeaver.Rewrite(output, runtime, Path.Combine(directory, "twice.dll")));
                Throws<NotSupportedException>(() => HintingWeaver.Rewrite(runtime, runtime, Path.Combine(directory, "wrong.dll")));
                Require(!File.Exists(Path.Combine(directory, "twice.dll")), "Double weaving produced output");

                var font = before.GetAllTypes().SelectMany(t => t.Methods).First(m => m.CilMethodBody?.Instructions.Any(i => i.Operand is IMethodDescriptor d && d.DeclaringType?.FullName == "SkiaSharp.SKFont" && d.Name == "set_Hinting") == true);
                var call = font.CilMethodBody!.Instructions.First(i => i.Operand is IMethodDescriptor d && d.DeclaringType?.FullName == "SkiaSharp.SKFont" && d.Name == "set_Hinting");
                ((MemberReference)call.Operand!).Name = "unsupported_Hinting";
                var unsupported = Path.Combine(directory, "unsupported.dll");
                before.Write(unsupported);
                Throws<InvalidOperationException>(() => HintingWeaver.Rewrite(unsupported, runtime, Path.Combine(directory, "bad.dll")));
                Require(!File.Exists(Path.Combine(directory, "bad.dll")), "Unsupported shape produced output");
                Console.WriteLine($"PASS {source}: {profile}, identity, source integrity, repeat/unsupported guards");
            }
            finally { Directory.Delete(directory, recursive: true); }
        }
        Console.WriteLine($"PASS hook contracts; {args.Length} assembly variants");
    }
}
