using Pubsmith.Core;
using SkiaSharp;

namespace Pubsmith.Rendering.Tests;

// Text layout behaviour, probed through the ink bounding box of rendered pixels (72 dpi: 1 px = 1 pt).
public class TextLayoutTests
{
    private const double FrameX = 20, FrameY = 20, FrameW = 200, FrameH = 150;

    private static SKRectI Ink(params Paragraph[] paragraphs) => Ink(new Insets(0, 0, 0, 0), paragraphs);

    private static SKRectI Ink(Insets insets, params Paragraph[] paragraphs)
    {
        var page = new Page(260, 220)
        {
            Elements = [new TextElement { Bounds = new Box(FrameX, FrameY, FrameW, FrameH), Insets = insets, Paragraphs = paragraphs }],
        };
        using var bmp = Exporter.RenderBitmap(page, 72, new RenderContext());
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red + c.Green + c.Blue > 3 * 200) continue;   // near-white: no ink
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
        return maxX < 0 ? SKRectI.Empty : new SKRectI(minX, minY, maxX, maxY);
    }

    private static Paragraph P(string text, TextAlignment align = TextAlignment.Left, double size = 20) => new()
    {
        Alignment = align, SpaceAfter = 0, LineSpacing = 1,
        Runs = [new TextRun(text, new TextStyle { Family = "Arial", Size = size })],
    };

    [Fact]
    public void LeftAligned_StartsAtFrameLeft()
    {
        var ink = Ink(P("HH"));
        Assert.InRange(ink.Left, FrameX, FrameX + 4);
        Assert.True(ink.Right < FrameX + FrameW / 2);
    }

    [Fact]
    public void RightAligned_EndsAtFrameRight()
    {
        var ink = Ink(P("HH", TextAlignment.Right));
        Assert.InRange(ink.Right, FrameX + FrameW - 4, FrameX + FrameW);
        Assert.True(ink.Left > FrameX + FrameW / 2);
    }

    [Fact]
    public void Centered_IsSymmetricInFrame()
    {
        var ink = Ink(P("HH", TextAlignment.Center));
        var mid = (ink.Left + ink.Right) / 2.0;
        Assert.InRange(mid, FrameX + FrameW / 2 - 3, FrameX + FrameW / 2 + 3);
    }

    [Fact]
    public void Insets_ShiftTheTextBlock()
    {
        var plain = Ink(P("HH"));
        var inset = Ink(new Insets(30, 25, 0, 0), P("HH"));
        Assert.InRange(inset.Left, plain.Left + 29, plain.Left + 31);
        Assert.InRange(inset.Top, plain.Top + 24, plain.Top + 26);
    }

    [Fact]
    public void Justified_NonLastLineReachesRightEdge()
    {
        // Wraps to 2+ lines; the first line must be stretched to the frame's right edge.
        var text = "HH HH HH HH HH HH HH HH HH HH";
        var left = Ink(P(text));
        var justified = Ink(P(text, TextAlignment.Justify));
        Assert.True(left.Right < FrameX + FrameW - 8, $"left-aligned right edge {left.Right} should be ragged");
        Assert.InRange(justified.Right, FrameX + FrameW - 4, FrameX + FrameW);
    }

    [Fact]
    public void Justified_SingleLine_IsNotStretched()
    {
        var ink = Ink(P("HH HH", TextAlignment.Justify));
        Assert.True(ink.Right < FrameX + FrameW / 2);
    }

    [Fact]
    public void ForcedBreak_StartsANewLine()
    {
        var one = Ink(P("HH HH"));
        var two = Ink(P("HH\nHH"));
        Assert.True(two.Height > one.Height * 1.8, $"one line {one.Height}px, broken {two.Height}px");
        Assert.True(two.Width < one.Width);
    }

    [Fact]
    public void ConsecutiveSpaces_WidenTheGap()
    {
        var single = Ink(P("H H"));
        var triple = Ink(P("H   H"));
        Assert.True(triple.Width > single.Width + 8);
    }

    [Fact]
    public void EmptyParagraph_AddsALine()
    {
        var without = Ink(P("HH"), P("HH"));
        var with = Ink(P("HH"), new Paragraph { LineSpacing = 1, SpaceAfter = 0 }, P("HH"));
        Assert.True(with.Height > without.Height + 8);
    }

    [Fact]
    public void EmptyParagraph_IsAsTallAsItsOwnFormatting()
    {
        static Paragraph Blank(double size) => new() { LineSpacing = 1, SpaceAfter = 0, Runs = [new TextRun("", new TextStyle { Family = "Arial", Size = size })] };
        var small = Ink(P("HH"), Blank(8), P("HH"));
        var large = Ink(P("HH"), Blank(40), P("HH"));
        // Arial's line spacing is about 1.15 em: a 40 pt blank line is about 37 pt taller than an 8 pt one.
        Assert.InRange(large.Height - small.Height, 32, 42);
    }

    [Fact]
    public void SpaceBeforeAndAfter_MoveFollowingParagraphs()
    {
        var tight = Ink(P("HH"), P("HH"));
        var spaced = Ink(P("HH") with { SpaceAfter = 30 }, P("HH") with { SpaceBefore = 20 });
        Assert.InRange(spaced.Height, tight.Height + 48, tight.Height + 52);
    }

    [Fact]
    public void ExactLineSpacing_SetsTheLinePitch()
    {
        // Two lines, 20 pt Arial: with exactly 60 pt spacing the second line's ink starts ~60 pt below the first.
        var loose = Ink(P("HH\nHH") with { ExactLineSpacing = 60 });
        var tight = Ink(P("HH\nHH") with { ExactLineSpacing = 24 });
        Assert.InRange(loose.Height - tight.Height, 34, 38);
    }

    [Theory]
    [InlineData(TextVerticalAlign.Middle)]
    [InlineData(TextVerticalAlign.Bottom)]
    public void VerticalAlign_MovesTheBlockDown(TextVerticalAlign align)
    {
        var top = InkIn(TextVerticalAlign.Top);
        var moved = InkIn(align);
        // Frame is 150 pt tall; a single 20 pt line sits at the top, centred, or at the bottom.
        var expectedShift = align == TextVerticalAlign.Middle ? (FrameH - top.Height) / 2 : FrameH - top.Height;
        Assert.InRange(moved.Top - top.Top, expectedShift - 12, expectedShift + 12);
        Assert.True(moved.Bottom <= FrameY + FrameH);
    }

    private static SKRectI InkIn(TextVerticalAlign align)
    {
        var page = new Page(260, 220)
        {
            Elements = [new TextElement { Bounds = new Box(FrameX, FrameY, FrameW, FrameH), Insets = new Insets(0, 0, 0, 0), VerticalAlign = align, Paragraphs = [P("HH")] }],
        };
        using var bmp = Exporter.RenderBitmap(page, 72, new RenderContext());
        int minY = int.MaxValue, maxY = -1;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y) is { Red: < 200 }) { minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
        return new SKRectI(0, minY, 1, maxY);
    }

    [Fact]
    public void AllCaps_DrawsCapitals_WithoutChangingTheText()
    {
        var lower = Ink(P("hh"));
        var para = new Paragraph { SpaceAfter = 0, LineSpacing = 1, Runs = [new TextRun("hh", new TextStyle { Family = "Arial", Size = 20, AllCaps = true })] };
        var caps = Ink(para);
        Assert.Equal(Ink(P("HH")).Width, caps.Width);   // drawn as "HH"
        Assert.NotEqual(lower.Width, caps.Width);
        Assert.Equal("hh", para.Runs[0].Text);          // the stored text is untouched
    }

    [Fact]
    public void Overflow_IsClippedToFrame()
    {
        var lots = string.Join(" ", Enumerable.Repeat("HHHH", 200));
        var ink = Ink(P(lots, size: 30));
        Assert.True(ink.Bottom <= FrameY + FrameH);
        Assert.True(ink.Right <= FrameX + FrameW);
    }

    [Fact]
    public void MixedRuns_DrawEachStyle()
    {
        var para = new Paragraph
        {
            SpaceAfter = 0,
            Runs =
            [
                new TextRun("small ", new TextStyle { Family = "Arial", Size = 8 }),
                new TextRun("BIG", new TextStyle { Family = "Arial", Size = 40, Color = new Rgba(255, 0, 0) }),
            ],
        };
        var page = new Page(260, 220) { Elements = [new TextElement { Bounds = new Box(0, 0, 260, 220), Paragraphs = [para] }] };
        using var bmp = Exporter.RenderBitmap(page, 72, new RenderContext());
        var red = 0;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y) is { Red: > 200, Green: < 60, Blue: < 60 }) red++;
        Assert.True(red > 100, $"only {red} red pixels");
        Assert.True(Ink(P("HH")).Height < 30);
    }
}
