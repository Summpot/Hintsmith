using System.Diagnostics;
using System.Reflection;
using Avalonia.Media;
using Hintsmith;
using SkiaSharp;

var type = typeof(SKFont).Assembly.GetType("Avalonia.Skia.GlyphRunImpl", true)!;
var instance = Activator.CreateInstance(type)!;
var fontMethod = type.GetMethod("CreateFont", BindingFlags.Instance | BindingFlags.NonPublic)!;
var parameter = Activator.CreateInstance(fontMethod.GetParameters()[0].ParameterType);
var font = (SKFont)fontMethod.Invoke(instance, new[] { parameter })!;
if (font.Hinting != SKFontHinting.Normal) throw new Exception("Woven fixture did not use Normal");
Hinting.SetHinting(HintingMode.Full);
font = (SKFont)fontMethod.Invoke(instance, new[] { parameter })!;
if (font.Hinting != SKFontHinting.Full) throw new Exception("Runtime switch failed");
var blob = type.GetMethod("GetTextBlob")!;
var parameters = blob.GetParameters().Select(p => Activator.CreateInstance(p.ParameterType)).ToArray();
parameters[parameters.Length - 1] = new RenderOptions { Throw = true };
try { blob.Invoke(instance, parameters); throw new Exception("Expected fixture exception"); }
catch (TargetInvocationException ex)
{
    var frame = new StackTrace(ex.InnerException!, true).GetFrames().Single(f => f.GetMethod()?.Name == "GetTextBlob");
    if (frame.GetFileLineNumber() == 0 || frame.GetFileName() == null) throw new Exception("Runtime could not load rewritten symbols");
    var lines = File.ReadAllLines(frame.GetFileName()!);
    if (!lines[frame.GetFileLineNumber() - 1].Contains("throw new InvalidOperationException"))
        throw new Exception("Exception mapped to the wrong source line: " + frame);
    Console.WriteLine("PASS runtime source line for woven method: " + frame.GetFileName() + ":" + frame.GetFileLineNumber());
}
