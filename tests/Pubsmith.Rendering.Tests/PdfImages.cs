using System.Text;
using System.Text.RegularExpressions;

namespace Pubsmith.Rendering.Tests;

/// <summary>The images a PDF written by SkPDF contains: each image XObject's size and filter, read from its dictionary.</summary>
internal static partial class PdfImages
{
    internal sealed record Image(int Width, int Height, string Filter);

    public static IReadOnlyList<Image> In(byte[] pdf)
    {
        // SkPDF writes object dictionaries uncompressed; an image's dictionary has /Subtype /Image, /Width and /Height.
        var text = Encoding.Latin1.GetString(pdf);
        var images = new List<Image>();
        foreach (Match m in Dictionary().Matches(text))
        {
            var dict = m.Value;
            if (!dict.Contains("/Subtype /Image")) continue;
            var w = Number("/Width", dict);
            var h = Number("/Height", dict);
            var filter = Regex.Match(dict, @"/Filter\s*/(\w+)") is { Success: true } f ? f.Groups[1].Value : "";
            images.Add(new Image(w, h, filter));
        }
        return images;
    }

    private static int Number(string key, string dict) => int.Parse(Regex.Match(dict, Regex.Escape(key) + @"\s+(\d+)").Groups[1].Value);

    [GeneratedRegex(@"<<(?:[^<>]|<<(?:[^<>]|<<[^<>]*>>)*>>)*>>", RegexOptions.Singleline)]
    private static partial Regex Dictionary();
}
