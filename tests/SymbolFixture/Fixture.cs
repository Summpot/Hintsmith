using Alias = System.String;
namespace SkiaSharp
{
    public enum SKFontHinting { None, Slight, Normal, Full }
    public enum SKFontEdging { Alias, Antialias, SubpixelAntialias }
    public sealed class SKTextBlob { }
    public sealed class SKFont
    {
        public SKFontHinting Hinting { get; set; }
        public bool ForceAutoHinting { get; set; }
        public SKFontEdging Edging { get; set; }
    }
}
namespace Avalonia.Media
{
    public enum TextHintingMode : byte { Unspecified, None, Light, Strong }
    public readonly record struct TextOptions { public TextHintingMode TextHintingMode { get; init; } }
    public readonly struct RenderOptions { public bool Throw { get; init; } }
}
namespace Avalonia.Skia
{
    using Avalonia.Media;
    using SkiaSharp;
    internal sealed class TwoLevelCache<TKey, TValue> where TKey : notnull
    {
        private readonly Dictionary<TKey, TValue> _items = new();
        public TValue GetOrAdd(TKey key, Func<TKey, TValue> factory)
        {
            if (!_items.TryGetValue(key, out var value)) _items[key] = value = factory(key);
            return value;
        }
    }
    internal sealed class GlyphRunImpl
    {
#if LEGACY
        private readonly SKTextBlob?[] _textBlobCache = new SKTextBlob?[3];
        public GlyphRunImpl() { _ = CreateFont(SKFontEdging.SubpixelAntialias); }
        public SKTextBlob GetTextBlob(RenderOptions options)
        {
            if (options.Throw) throw new InvalidOperationException("fixture legacy");
            SKFontEdging edging = SKFontEdging.SubpixelAntialias;
            if (_textBlobCache[(int)edging] is null)
            {
                var font = CreateFont(edging);
                _textBlobCache[(int)edging] = new SKTextBlob();
            }
            return _textBlobCache[(int)edging]!;
        }
        private SKFont CreateFont(SKFontEdging edging)
        {
            const int value = 10;
            var local = value + (int)edging;
            var font = new SKFont();
            font.Hinting = SKFontHinting.Full;
            font.Edging = edging;
            GC.KeepAlive(local);
            return font;
        }
#else
        private readonly TwoLevelCache<TextOptions, SKTextBlob> _textBlobCache = new();
        public GlyphRunImpl() { _ = CreateFont(new TextOptions { TextHintingMode = TextHintingMode.Strong }); }
        public SKTextBlob GetTextBlob(TextOptions options, RenderOptions render)
        {
            if (render.Throw) throw new InvalidOperationException("fixture modern");
            const int localConstant = 42;
            Alias local = "document one";
#line 100 "other-source.cs"
            if (local.Length == localConstant)
            {
                int scoped = local.Length + localConstant;
                GC.KeepAlive(scoped);
            }
            return _textBlobCache.GetOrAdd(options, key => { _ = CreateFont(options); return new SKTextBlob(); });
#line default
        }
        private SKFont CreateFont(TextOptions options)
        {
            var local = options.TextHintingMode;
            var font = new SKFont();
            font.Hinting = local == TextHintingMode.None ? SKFontHinting.None : SKFontHinting.Full;
            font.ForceAutoHinting = local == TextHintingMode.Light;
            return font;
        }
#endif
    }
    public static class Unchanged
    {
        public static async Task<int> AsyncMethod() { await Task.Yield(); return 42; }
        public static IEnumerable<int> Iterator() { yield return 1; yield return 2; }
    }
}
