namespace Pubsmith.PubReader.OfficeArt;

/// <summary>Publisher's shape anchor: edges in EMU (12,700 per point) measured from the page centre.</summary>
public readonly record struct AnchorEmu(int Left, int Top, int Right, int Bottom);

/// <summary>
/// An OfficeArtCOLORREF (MS-ODRAW 2.2.2): red in the low byte, flags in the high byte. Publisher uses flag 0x08
/// (MS-ODRAW's scheme index) for its document palette and 0x10 for tints and shades; see <see cref="ColorResolver"/>.
/// </summary>
public readonly record struct ColorRef(byte R, byte G, byte B, byte Flags)
{
    public bool IsSchemeIndex => (Flags & 0x08) != 0;

    public static ColorRef From(uint v) => new((byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24));
}

/// <summary>One OfficeArt shape (OfficeArtSpContainer) with its FSP flags, properties and anchor.</summary>
public sealed record EscherShape
{
    // Property ids (MS-ODRAW 2.3)
    public const ushort PropRotation = 0x0004, PropPib = 0x0104, PropCropTop = 0x0100, PropCropBottom = 0x0101,
        PropCropLeft = 0x0102, PropCropRight = 0x0103, PropGtextUnicode = 0x00C0, PropGtextFont = 0x00C5,
        PropFillColor = 0x0181, PropFillOpacity = 0x0182, PropFillFlags = 0x01BF, PropLineColor = 0x01C0,
        PropLineOpacity = 0x01C1, PropLineWidth = 0x01CB, PropLineDashing = 0x01CE, PropLineFlags = 0x01FF,
        PropShadowFlags = 0x023F, PropTextId = 0x0080, PropTextLeft = 0x0081, PropTextTop = 0x0082,
        PropTextRight = 0x0083, PropTextBottom = 0x0084;

    public required int Offset { get; init; }
    public required uint ShapeId { get; init; }
    /// <summary>MSOSPT shape type (1 rectangle, 3 ellipse, 75 picture frame, 136+ WordArt, 202 text box...).</summary>
    public required ushort ShapeType { get; init; }
    public required uint Flags { get; init; }
    /// <summary>Primary and secondary FOPT properties.</summary>
    public required IReadOnlyDictionary<ushort, uint> Props { get; init; }
    public required IReadOnlyDictionary<ushort, byte[]> Complex { get; init; }
    /// <summary>
    /// TertiaryFOPT (0xF122) properties, kept apart: Publisher reuses ids there with other meanings (e.g. 0x01FF
    /// holds 0x400000 while the primary 0x01FF says the line is off), so merging them corrupts the primary set.
    /// </summary>
    public IReadOnlyDictionary<ushort, uint> Tertiary { get; init; } = new Dictionary<ushort, uint>();
    public AnchorEmu? Anchor { get; init; }
    /// <summary>Anchor in the parent group's coordinate system (OfficeArtChildAnchor 0xF00F), for group children.</summary>
    public AnchorEmu? ChildAnchor { get; init; }
    /// <summary>A group leader's own coordinate system (OfficeArtFSPGR 0xF009).</summary>
    public AnchorEmu? GroupCoordinates { get; init; }
    /// <summary>Publisher seqnum from ClientData (0xF011, id 0x6801): the key into the Contents shape/page records.</summary>
    public uint? Seqnum { get; init; }
    /// <summary>Nesting depth inside group containers (0 = top level).</summary>
    public int Depth { get; init; }
    /// <summary>For a group leader: the shapes (and nested groups) it contains, in drawing order.</summary>
    public IReadOnlyList<EscherShape> Children { get; init; } = [];
    /// <summary>A property table declared more complex data than its record holds; the missing values are empty.</summary>
    public bool Truncated { get; init; }

    public bool IsGroup => (Flags & 0x001) != 0;
    public bool IsPatriarch => (Flags & 0x004) != 0;
    /// <summary>fDeleted: the shape was deleted in Publisher but is still in the stream. It is not drawn.</summary>
    public bool IsDeleted => (Flags & 0x008) != 0;
    public bool FlipH => (Flags & 0x040) != 0;
    public bool FlipV => (Flags & 0x080) != 0;

    /// <summary>Degrees as stored (16.16 fixed point, signed). Publisher reports 270 for a stored -90.</summary>
    public double RotationDegrees => Props.TryGetValue(PropRotation, out var v) ? (int)v / 65536.0 : 0;

    public ColorRef? FillColor => Props.TryGetValue(PropFillColor, out var v) ? ColorRef.From(v) : null;
    public ColorRef? LineColor => Props.TryGetValue(PropLineColor, out var v) ? ColorRef.From(v) : null;

    /// <summary>1-based index into the picture store (BStore), if this shape shows a picture.</summary>
    public int? PictureIndex => Props.TryGetValue(PropPib, out var v) && v is > 0 and <= int.MaxValue ? (int)v : null;

    public uint? Get(ushort id) => Props.TryGetValue(id, out var v) ? v : null;

    /// <summary>
    /// Boolean-property bit with its "use" bit (MS-ODRAW: bit n is the value, bit n+16 says it is set).
    /// Returns the default when the use bit is clear.
    /// </summary>
    public bool Flag(ushort id, int bit, bool defaultValue)
    {
        if (!Props.TryGetValue(id, out var v)) return defaultValue;
        var mask = 1u << bit;
        return (v & (mask << 16)) != 0 ? (v & mask) != 0 : defaultValue;
    }

    public string? ComplexString(ushort id) =>
        Complex.TryGetValue(id, out var b) ? System.Text.Encoding.Unicode.GetString(b).TrimEnd('\0') : null;
}
