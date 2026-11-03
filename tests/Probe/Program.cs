using System.Reflection;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Skia;
using Hintsmith;
using SkiaSharp;

typeof(Point).Assembly.GetType("Avalonia.Platform.StandardRuntimePlatformServices", true)!
    .GetMethod("Register", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, new object?[] { typeof(Program).Assembly });
SkiaPlatform.Initialize();
var type = typeof(SkiaPlatform).Assembly.GetType("Avalonia.Skia.GlyphRunImpl", true)!;
var ctor = type.GetConstructors().Single();
var face = Typeface.Default.GlyphTypeface;
var platformFace = ctor.GetParameters()[0].ParameterType.IsInstanceOfType(face) ? (object)face :
    face.GetType().GetProperty("PlatformImpl", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(face)!;
using var glyph = (IDisposable)ctor.Invoke(new object[] { platformFace, 14d, new[] { new GlyphInfo(43, 0, 14) }, default(Point) });
var getBlob = type.GetMethod("GetTextBlob")!;
var createFont = type.GetMethod("CreateFont", BindingFlags.Instance | BindingFlags.NonPublic);
var modern = getBlob.GetParameters().Length == 2;
object Options(byte mode, TextRenderingMode rendering = TextRenderingMode.Unspecified)
{
    var options = Activator.CreateInstance(getBlob.GetParameters()[0].ParameterType)!;
    var property = options.GetType().GetProperty("TextHintingMode")!;
    property.SetValue(options, Enum.ToObject(property.PropertyType, mode));
    options.GetType().GetProperty("TextRenderingMode")!.SetValue(options, rendering);
    return options;
}
#pragma warning disable CS0618 // Legacy RenderOptions API is intentionally exercised for Avalonia 11.
SKTextBlob Blob(byte original = 0, TextRenderingMode rendering = TextRenderingMode.Unspecified) => (SKTextBlob)getBlob.Invoke(glyph,
    modern ? new[] { Options(original, rendering), (object)default(RenderOptions) } : new object[] { new RenderOptions { TextRenderingMode = rendering } })!;
#pragma warning restore CS0618
void Equal<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{message}: expected {expected}, got {actual}");
}
void CheckFont(HintingMode expected, byte original = 0)
{
    if (createFont == null)
    {
        // 11.0 keeps the SKFont on its glyph typeface; GetTextBlob configured it.
        // A cache hit does not touch that shared font. Use a fresh run to force creation.
        using var fresh = (IDisposable)ctor.Invoke(new object[] { platformFace, 14d, new[] { new GlyphInfo(43, 0, 14) }, default(Point) });
        getBlob.Invoke(fresh, new object[] { default(RenderOptions) });
        var holder = type.GetField("_glyphTypefaceImpl", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(fresh)!;
        var shared = (SKFont)holder.GetType().GetProperty("SKFont")!.GetValue(holder)!;
        Equal((int)expected, (int)shared.Hinting, "Shared font hinting");
        Equal(SKFontEdging.SubpixelAntialias, shared.Edging, "Decoded edging");
        return;
    }
    using var font = (SKFont)createFont.Invoke(glyph, new[] { modern ? Options(original) : (object)SKFontEdging.SubpixelAntialias })!;
    Equal((int)expected, (int)font.Hinting, "Font hinting");
    Equal(SKFontEdging.SubpixelAntialias, font.Edging, "Decoded edging");
    if (modern) Equal(expected == HintingMode.Slight, font.ForceAutoHinting, "Auto hinting");
}
Equal(HintingMode.Normal, Hinting.Current, "Initial policy");
var normal = Blob();
CheckFont(HintingMode.Normal);
Equal(normal, Blob(), "Cache hit");
foreach (var mode in new[] { HintingMode.None, HintingMode.Slight, HintingMode.Full })
{
    Hinting.SetHinting(mode);
    var blob = Blob();
    if (ReferenceEquals(normal, blob)) throw new Exception("Stale cache after mode change");
    CheckFont(mode);
    using var bitmap = new SKBitmap(80, 40);
    using var canvas = new SKCanvas(bitmap);
    using var paint = new SKPaint { Color = SKColors.Black };
    canvas.Clear(SKColors.White);
    canvas.DrawText(blob, 5, 25, paint);
    if (!bitmap.Pixels.Any(p => p != SKColors.White)) throw new Exception("Raster draw produced no text");
}
foreach (var rendering in new[] { TextRenderingMode.Alias, TextRenderingMode.Antialias, TextRenderingMode.SubpixelAntialias })
{
    foreach (var mode in new[] { HintingMode.None, HintingMode.Slight, HintingMode.Normal, HintingMode.Full })
    {
        Hinting.SetHinting(mode);
        var blob = Blob(0, rendering);
        Equal(blob, Blob(0, rendering), "Cache hit for explicit rendering mode");
        if (createFont != null)
        {
            var edging = rendering == TextRenderingMode.Alias ? SKFontEdging.Alias : rendering == TextRenderingMode.Antialias ? SKFontEdging.Antialias : SKFontEdging.SubpixelAntialias;
            using var font = (SKFont)createFont.Invoke(glyph, new[] { modern ? Options(0, rendering) : (object)edging })!;
            Equal((int)mode, (int)font.Hinting, "Hinting with explicit rendering mode");
            Equal(edging, font.Edging, "Preserved explicit edging");
            Equal(rendering != TextRenderingMode.Alias, font.Subpixel, "Preserved subpixel policy");
        }
    }
}
Hinting.RestoreAvaloniaDefaults();
Blob();
CheckFont(HintingMode.Full);
if (modern)
{
    Blob(1); CheckFont(HintingMode.None, 1);
    Blob(2); CheckFont(HintingMode.Slight, 2);
    Hinting.SetHinting(HintingMode.Normal);
    Blob(2); CheckFont(HintingMode.Normal, 2);
}
Hinting.SetHinting(HintingMode.Normal);
Blob(); CheckFont(HintingMode.Normal);
try { Hinting.SetHinting((HintingMode)99); throw new Exception("Invalid mode accepted"); }
catch (ArgumentOutOfRangeException) { }
Console.WriteLine($"PASS Avalonia {typeof(SkiaPlatform).Assembly.GetName().Version}: Normal, runtime switching, cache, defaults, native raster draw");
