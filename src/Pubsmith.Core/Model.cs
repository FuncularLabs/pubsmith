using System.Text.Json.Serialization;

namespace Pubsmith.Core;

// Units throughout are points (1/72 in). Page origin is top-left, y grows downward (Publisher's convention).

/// <summary>A Pubsmith document: an ordered list of pages.</summary>
public sealed record PubsmithDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string Name { get; init; } = "";
    public IReadOnlyList<Page> Pages { get; init; } = [];
}

/// <summary>One page. Elements are drawn in list order (first = backmost).</summary>
public sealed record Page(double Width, double Height)
{
    public IReadOnlyList<Element> Elements { get; init; } = [];
}

/// <summary>An axis-aligned frame before rotation.</summary>
public readonly record struct Box(double X, double Y, double Width, double Height)
{
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ShapeElement), "shape")]
[JsonDerivedType(typeof(ImageElement), "image")]
[JsonDerivedType(typeof(TextElement), "text")]
public abstract record Element
{
    public Box Bounds { get; init; }

    /// <summary>Degrees, clockwise-positive, about the centre of <see cref="Bounds"/> (Publisher's convention).</summary>
    public double Rotation { get; init; }

    /// <summary>A hard-edged copy of the element's silhouette, drawn behind it at an offset.</summary>
    public Shadow? Shadow { get; init; }
}

/// <param name="Color">Shadow colour; its alpha is the shadow's opacity.</param>
/// <param name="OffsetX">Points, in page coordinates (positive = right).</param>
/// <param name="OffsetY">Points, in page coordinates (positive = down).</param>
public sealed record Shadow(Rgba Color, double OffsetX, double OffsetY);

public enum ShapeKind { Rectangle, Ellipse }

public sealed record Stroke(Rgba Color, double Width);

public sealed record ShapeElement : Element
{
    public ShapeKind Kind { get; init; } = ShapeKind.Rectangle;
    public Rgba? Fill { get; init; }
    public Stroke? Stroke { get; init; }
}

/// <summary>
/// Crop as fractions (0-1) of the SOURCE image removed from each side. Publisher's CropLeft etc. are points
/// of the unscaled picture, which depend on the image's native size; fractions do not.
/// </summary>
public sealed record Crop
{
    public Crop(double left, double top, double right, double bottom)
    {
        Check(left, nameof(left)); Check(top, nameof(top)); Check(right, nameof(right)); Check(bottom, nameof(bottom));
        if (left + right >= 1) throw new ArgumentException(FormattableString.Invariant($"left + right crop ({left} + {right}) removes the whole image width."));
        if (top + bottom >= 1) throw new ArgumentException(FormattableString.Invariant($"top + bottom crop ({top} + {bottom}) removes the whole image height."));
        (Left, Top, Right, Bottom) = (left, top, right, bottom);
    }

    public double Left { get; }
    public double Top { get; }
    public double Right { get; }
    public double Bottom { get; }

    private static void Check(double v, string name)
    {
        if (double.IsNaN(v) || v < 0 || v >= 1) throw new ArgumentOutOfRangeException(name, v, "Crop fractions must be in [0, 1).");
    }
}

public sealed record ImageElement : Element
{
    /// <summary>Path relative to the document file (or absolute).</summary>
    public string Source { get; init; } = "";
    public Crop? Crop { get; init; }
    public Stroke? Stroke { get; init; }
    /// <summary>Clip the picture (and its border) to this shape; null = the rectangular frame.</summary>
    public ShapeKind? Mask { get; init; }
}

public enum TextAlignment { Left, Center, Right, Justify }

public sealed record TextStyle
{
    public string Family { get; init; } = "Calibri";   // Publisher's default text box font
    public double Size { get; init; } = 10;
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    /// <summary>Displayed in capitals; the stored text keeps the case the user typed (Publisher's "All caps").</summary>
    public bool AllCaps { get; init; }
    public Rgba Color { get; init; } = new(0, 0, 0);
}

public sealed record TextRun(string Text, TextStyle Style);

public sealed record Paragraph
{
    public TextAlignment Alignment { get; init; } = TextAlignment.Left;
    /// <summary>Multiple of the font's natural line spacing. Publisher's default is 1.19.</summary>
    public double LineSpacing { get; init; } = 1.19;
    /// <summary>When set, every line is exactly this many points apart (Publisher "Exactly"), overriding <see cref="LineSpacing"/>.</summary>
    public double? ExactLineSpacing { get; init; }
    public double SpaceBefore { get; init; }
    public double SpaceAfter { get; init; } = 6;
    public IReadOnlyList<TextRun> Runs { get; init; } = [];
}

public sealed record Insets(double Left, double Top, double Right, double Bottom)
{
    /// <summary>Publisher text box default: 0.04 in on every side.</summary>
    public static Insets PublisherDefault { get; } = new(2.88, 2.88, 2.88, 2.88);
}

public enum TextVerticalAlign { Top, Middle, Bottom }

/// <summary>How text fills its frame. Stretch scales the glyphs to fill the inner frame exactly (WordArt).</summary>
public enum TextFit { None, Stretch }

/// <summary>
/// WordArt text shapes. Path kinds (ArchUp, ArchDown, Circle, Button) lay glyphs along an ellipse inside the
/// frame; the others bend the stretched text between a top and a bottom curve (an envelope).
/// </summary>
public enum WarpKind
{
    None, ArchUp, ArchDown, Circle, Button,
    CanUp, CanDown, SlantUp, SlantDown, CurveUp, CurveDown,
    Inflate, Deflate, InflateTop, InflateBottom, DeflateTop, DeflateBottom,
    Wave1, Wave2, Triangle, TriangleInverted, Chevron, ChevronInverted,
}

/// <param name="Adjust">Shape-specific amount (0-1 envelope depth); null = the preset's default.</param>
public sealed record TextWarp(WarpKind Kind, double? Adjust = null);

public sealed record TextElement : Element
{
    public Insets Insets { get; init; } = Insets.PublisherDefault;
    public TextVerticalAlign VerticalAlign { get; init; } = TextVerticalAlign.Top;
    public TextFit Fit { get; init; } = TextFit.None;
    /// <summary>WordArt warp; applies when <see cref="Fit"/> is Stretch.</summary>
    public TextWarp? Warp { get; init; }
    /// <summary>Glyph outline (WordArt line), drawn over the fill.</summary>
    public Stroke? Outline { get; init; }
    public IReadOnlyList<Paragraph> Paragraphs { get; init; } = [];
}
