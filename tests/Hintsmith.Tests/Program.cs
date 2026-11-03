using AsmResolver.DotNet;
using Hintsmith.Tasks;

if (args[0] == "weave")
{
    Console.WriteLine(HintingWeaver.Rewrite(args[1], args[2], args[3]));
    return;
}

foreach (var path in args)
{
    var module = ModuleDefinition.FromFile(path);
    Console.WriteLine($"=== {path} / {module.Assembly!.Version} ===");
    foreach (var t in module.GetAllTypes().Where(t => t.FullName.Contains("GlyphRunImpl")))
    foreach (var m in t.Methods.Where(m => m.CilMethodBody != null))
    {
        Console.WriteLine(m.FullName);
        foreach (var i in m.CilMethodBody!.Instructions) Console.WriteLine(i);
    }
}
