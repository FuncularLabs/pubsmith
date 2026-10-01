using Pubsmith.Core;

namespace Pubsmith.Rendering.Tests;

/// <summary>
/// Hand-built models of test-corpus pages, transcribed from New-FeatureCorpus.ps1 (the script that made the
/// .pub files) plus Publisher defaults measured by COM on 2026-09-27: new shapes get a 2 pt black line;
/// pictures have no line; text boxes are Calibri 10 with 2.88 pt insets; crop is points of the unscaled
/// picture (the 1024 px sample picture is 768 pt, so a 54 pt crop is 0.0703125).
/// </summary>
internal static class CorpusModels
{
    private static readonly Stroke DefaultLine = new(new Rgba(0, 0, 0), 2);

    public static Page F01(bool mutate = false)
    {
        var elements = new List<Element>();
        if (!mutate)   // mutation: the inset frame is missing
            elements.Add(new ShapeElement { Bounds = new Box(18, 18, 378, 306), Stroke = DefaultLine });
        foreach (var (x, y) in new[] { (0.0, 0.0), (396.0, 0.0), (0.0, 324.0), (396.0, 324.0) })
            elements.Add(new ShapeElement { Bounds = new Box(x, y, 18, 18), Fill = new Rgba(200, 0, 0), Stroke = DefaultLine });
        elements.Add(Text(107, 150, 200, 40, "F01 5.75 x 4.75 in", TextAlignment.Center));
        return new Page(414, 342) { Elements = elements };
    }

    public static Page F04(bool mutate = false)
    {
        var elements = new List<Element>();
        if (!mutate)   // mutation: the coloured background is missing
            elements.Add(new ShapeElement { Bounds = new Box(36, 36, 540, 300), Fill = new Rgba(255, 200, 120), Stroke = DefaultLine });
        elements.Add(Image(54, 54, 216, 216, "sample-picture.png"));
        elements.Add(Image(300, 120, 240, 73, "sample-strip.png"));
        const double c = 54.0 / 768.0;
        elements.Add(Image(69.1875, 395.1875, 185.625, 185.6258, "sample-picture.png") with { Crop = new Crop(c, c, c, c) });
        elements.Add(Image(300, 380, 260, 130, "sample-picture.png"));
        return new Page(612, 792) { Elements = elements };
    }

    public static Page F12(bool mutate = false)
    {
        var pictureX = mutate ? 302 : 320;   // mutation: image moved 0.25 in left
        return new Page(414, 342)
        {
            Elements =
            [
                new ShapeElement { Bounds = new Box(-9, -9, 432, 360), Fill = new Rgba(255, 150, 40) },
                Image(pictureX, 100, 150, 150, "sample-picture.png"),
                Text(27, 27, 250, 30, "Inside 0.375in safe area", TextAlignment.Left),
            ],
        };
    }

    private static ImageElement Image(double x, double y, double w, double h, string file) =>
        new() { Bounds = new Box(x, y, w, h), Source = Path.Combine("assets", file) };

    private static TextElement Text(double x, double y, double w, double h, string s, TextAlignment align) => new()
    {
        Bounds = new Box(x, y, w, h),
        Paragraphs = [new Paragraph { Alignment = align, Runs = [new TextRun(s, new TextStyle())] }],
    };
}
