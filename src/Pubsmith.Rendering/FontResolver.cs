using SkiaSharp;

namespace Pubsmith.Rendering;

/// <param name="RequestedFamily">What the document asked for.</param>
/// <param name="IsSubstitute">True when <paramref name="Typeface"/> is not the requested family.</param>
public sealed record ResolvedFont(SKTypeface Typeface, string RequestedFamily, bool IsSubstitute);

/// <summary>
/// Maps a family + style to an installed typeface. Publisher documents often name a face by its full name
/// ("Arial Black", "Gill Sans Ultra Bold") while the system exposes the family ("Arial") with a weight, so a
/// trailing weight/width word is split off and retried before giving up. A family that is not installed (for
/// example a Microsoft 365 cloud font) falls back to Calibri, then the system default, flagged as a substitute.
/// </summary>
public sealed class FontResolver
{
    private const string FallbackFamily = "Calibri";
    private readonly Dictionary<(string, bool, bool), ResolvedFont> _cache = [];
    private readonly SKFontManager _manager = SKFontManager.Default;

    // Trailing words of a face name, longest first, with their CSS weight or width meaning.
    private static readonly (string Suffix, int Weight, int Width)[] Suffixes =
    [
        ("Ultra Bold", 800, 5), ("Extra Bold", 800, 5), ("ExtraBold", 800, 5), ("Semi Bold", 600, 5), ("SemiBold", 600, 5),
        ("Semibold", 600, 5), ("Demi Bold", 600, 5), ("Demibold", 600, 5), ("Extra Light", 200, 5), ("ExtraLight", 200, 5),
        ("Semilight", 350, 5), ("Black", 900, 5), ("Heavy", 900, 5), ("Bold", 700, 5), ("Medium", 500, 5),
        ("Light", 300, 5), ("Thin", 100, 5), ("Narrow", 400, 3), ("Condensed", 400, 3),
    ];

    /// <summary>Splits "Arial Black" into ("Arial", 900, normal width); null when the name has no style suffix.</summary>
    public static (string BaseFamily, int Weight, int Width)? SplitStyle(string family)
    {
        foreach (var (suffix, weight, width) in Suffixes)
            if (family.Length > suffix.Length + 1 && family.EndsWith(" " + suffix, StringComparison.OrdinalIgnoreCase))
                return (family[..^(suffix.Length + 1)].TrimEnd(), weight, width);
        return null;
    }

    public ResolvedFont Resolve(string family, bool bold, bool italic)
    {
        var key = (family.ToUpperInvariant(), bold, italic);
        if (_cache.TryGetValue(key, out var hit)) return hit;

        var slant = italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright;
        var style = new SKFontStyle(bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, slant);

        var result = TryExact(family, style) is { } exact
            ? new ResolvedFont(exact, family, IsSubstitute: false)
            : TryStyled(family, bold, slant) is { } styled
                ? new ResolvedFont(styled, family, IsSubstitute: false)
                : new ResolvedFont(_manager.MatchFamily(FallbackFamily, style) ?? SKTypeface.Default, family, IsSubstitute: true);
        _cache[key] = result;
        return result;
    }

    private SKTypeface? TryExact(string family, SKFontStyle style)
    {
        var tf = _manager.MatchFamily(family, style);
        if (tf is not null && string.Equals(tf.FamilyName, family, StringComparison.OrdinalIgnoreCase)) return tf;
        tf?.Dispose();
        return null;
    }

    /// <summary>"Arial Black" -> family "Arial" at weight 900; accepted only if the system really has that family.</summary>
    private SKTypeface? TryStyled(string family, bool bold, SKFontStyleSlant slant)
    {
        if (SplitStyle(family) is not var (baseFamily, weight, width)) return null;
        var target = bold ? Math.Max(weight, 700) : weight;   // "Arial Narrow" in bold asks for weight 700, not 400
        var tf = _manager.MatchFamily(baseFamily, new SKFontStyle(target, width, slant));
        if (tf is not null && string.Equals(tf.FamilyName, baseFamily, StringComparison.OrdinalIgnoreCase)
            && Math.Abs(tf.FontStyle.Weight - target) <= 200 && (width == 5 || tf.FontStyle.Width < 5))
            return tf;
        tf?.Dispose();
        return null;
    }
}
