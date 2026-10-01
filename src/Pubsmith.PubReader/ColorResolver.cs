namespace Pubsmith.PubReader;

/// <param name="Approximate">True when the reference could not be resolved exactly (unknown/modified form).</param>
public readonly record struct RgbColor(byte R, byte G, byte B, bool Approximate = false)
{
    public static readonly RgbColor Black = new(0, 0, 0);
}

/// <summary>
/// Resolves Publisher colour references (docs/format/pub-format-notes.md §3): literal RGB with red in the low
/// byte; high byte 0x08 = index into the document palette (whose entries are literal 0x00BBGGRR values),
/// 0x10 = a tint or shade of another colour. Other flag bytes are resolved as RGB but marked approximate.
/// </summary>
public sealed class ColorResolver(IReadOnlyList<uint> palette)
{
    public IReadOnlyList<uint> Palette { get; } = palette;

    public RgbColor Resolve(uint reference)
    {
        switch (reference >> 24)
        {
            case 0x08:
                var index = (int)(reference & 0xFFFFFF);
                return index < Palette.Count ? Literal(Palette[index]) : RgbColor.Black with { Approximate = true };
            case 0x10:
                // A tint/shade of another colour; only meaningful with its base (e.g. a gradient's first colour).
                // Standalone, it cannot be resolved exactly.
                return new RgbColor(128, 128, 128, Approximate: true);
            case 0x00 or 0x02 or 0x04 or 0x06:
                // Literal RGB; MS-ODRAW's fPaletteRGB (0x02) and fSystemRGB (0x04) still carry the RGB value.
                return Literal(reference);
            default:
                // A palette index (0x01), system colour index or unknown flags: the low bytes are not a colour.
                return Literal(reference) with { Approximate = true };
        }
    }

    private static RgbColor Literal(uint v) => new((byte)v, (byte)(v >> 8), (byte)(v >> 16));
}
