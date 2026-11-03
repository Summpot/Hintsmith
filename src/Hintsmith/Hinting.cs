using System.ComponentModel;
using System.Threading;

namespace Hintsmith;

public enum HintingMode
{
    AvaloniaDefault = -1,
    None = 0,
    Slight = 1,
    Normal = 2,
    Full = 3
}

/// <summary>Process-wide hinting policy. Changes take effect on the next text draw.</summary>
public static class Hinting
{
    private static int _mode = (int)HintingMode.Normal;

    public static HintingMode Current => (HintingMode)Volatile.Read(ref _mode);

    /// <summary>Changes the policy atomically. Request a redraw on the Avalonia UI thread afterwards.</summary>
    public static void SetHinting(HintingMode mode)
    {
        if (mode < HintingMode.AvaloniaDefault || mode > HintingMode.Full)
            throw new ArgumentOutOfRangeException(nameof(mode));
        Volatile.Write(ref _mode, (int)mode);
    }

    public static void RestoreAvaloniaDefaults() => SetHinting(HintingMode.AvaloniaDefault);
}

/// <summary>Versioned IL ABI; public for calls from the rewritten Avalonia.Skia assembly.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class HookV1
{
    // Snapshot once before entering a cache. Encoded keys carry the same snapshot
    // into CreateFont even if another thread changes the global setting meanwhile.
    public static int SnapshotLegacy()
    {
        var mode = (int)Hinting.Current;
        return mode < 0 ? 3 : mode;
    }

    public static int PackEdging(int edging, int mode) => 16 + mode * 3 + edging;
    public static int DecodeEdging(int token) => (token - 16) % 3;
    public static int LegacyHinting(int original, int token) => (token - 16) / 3;
    public static int NormalizeEdging(int edging) => edging >= 16 ? edging : PackEdging(edging, SnapshotLegacy());

    public static byte NormalizeTextHinting(byte original)
    {
        if (original >= 240 && original <= 243) return original;
        var mode = (int)Hinting.Current;
        if (mode < 0) mode = original == 1 ? 0 : original == 2 ? 1 : 3;
        return (byte)(240 + mode);
    }

    public static int TextHinting(int original, byte token) => token - 240;
    public static bool ForceAutoHinting(bool original, byte token) => token == 241;
}
