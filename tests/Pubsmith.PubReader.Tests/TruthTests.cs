using System.Text.Json;
using Pubsmith.Core;
using Xunit.Abstractions;

namespace Pubsmith.PubReader.Tests;

// AC2/AC3/AC4/AC6 against Publisher's own layout export (WI-002) for every diff-corpus variant: page count, and the
// native importer's editable shapes and text boxes against Publisher's frames (0.1 pt), rotations (0.1 deg), colours
// and text. Variants whose export flattened every element to a picture compare page count only.
public class TruthTests(ITestOutputHelper output)
{
    public static TheoryData<string> Variants()
    {
        var data = new TheoryData<string>();
        foreach (var f in Directory.GetFiles(Path.Combine(Fixtures.Dir, "truth"), "*.json").OrderBy(f => f))
            data.Add(Path.GetFileNameWithoutExtension(f));
        return data;
    }

    private sealed record TruthEl(string Type, Box Bounds, double Rotation, string? Fill, string? Text);

    private static List<TruthEl> TruthPage(string variant, int page)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures.Dir, "truth", variant + ".json")));
        var p = doc.RootElement.GetProperty("pages")[page];
        return p.GetProperty("elements").EnumerateArray()
            .Where(e => e.GetProperty("type").GetString() is "shape" or "text")
            .Select(e =>
            {
                var b = e.GetProperty("bounds");
                var box = new Box(b.GetProperty("x").GetDouble(), b.GetProperty("y").GetDouble(), b.GetProperty("width").GetDouble(), b.GetProperty("height").GetDouble());
                string? fill = e.TryGetProperty("fill", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
                string? text = e.GetProperty("type").GetString() == "text"
                    ? string.Concat(e.GetProperty("paragraphs").EnumerateArray().SelectMany(pp => pp.GetProperty("runs").EnumerateArray()).Select(r => r.GetProperty("text").GetString()))
                    : null;
                return new TruthEl(e.GetProperty("type").GetString()!, box, e.GetProperty("rotation").GetDouble(), fill, text);
            }).ToList();
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public void EditableElements_MatchPublisher(string variant)
    {
        var (group, name) = (variant.Split("__")[0], variant.Split("__")[1]);
        var imported = PubImporter.Import(Fixtures.Pub(group, name));
        using var truthDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures.Dir, "truth", variant + ".json")));
        Assert.Equal(truthDoc.RootElement.GetProperty("pages").GetArrayLength(), imported.Document.Pages.Count);

        for (var pi = 0; pi < imported.Document.Pages.Count; pi++)
        {
            var truth = TruthPage(variant, pi);
            var ours = imported.Document.Pages[pi].Elements.Where(e => e is ShapeElement or TextElement).ToList();
            foreach (var t in truth)
            {
                // Find our element of the same kind closest to the truth frame.
                var candidates = ours.Where(o => (o is TextElement) == (t.Type == "text")).ToList();
                Assert.True(candidates.Count > 0, $"{variant} p{pi + 1}: no {t.Type} element for truth at {t.Bounds}");
                var best = candidates.MinBy(o => Dist(o.Bounds, t.Bounds))!;
                var d = Dist(best.Bounds, t.Bounds);
                output.WriteLine($"{variant} p{pi + 1} {t.Type}: truth {Fmt(t.Bounds)} rot {t.Rotation:F1}; ours {Fmt(best.Bounds)} rot {best.Rotation:F1}; dist {d:F2}");
                Assert.True(d <= 0.1, $"{variant} p{pi + 1} {t.Type}: frame off by {d:F3} pt (truth {Fmt(t.Bounds)}, ours {Fmt(best.Bounds)})");
                var rot = ((best.Rotation - t.Rotation) % 360 + 360) % 360;
                Assert.True(rot < 0.1 || rot > 359.9, $"{variant} {t.Type}: rotation {best.Rotation:F2} vs {t.Rotation:F2}");
                if (t.Fill is not null && best is ShapeElement s)
                    Assert.Equal(t.Fill[..7], s.Fill?.ToString()[..7]);
                if (t.Text is not null && best is TextElement te)
                    Assert.Equal(t.Text, string.Concat(te.Paragraphs.SelectMany(p => p.Runs).Select(r => r.Text)));
            }
        }
    }

    private static double Dist(Box a, Box b) => Math.Max(Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y)), Math.Max(Math.Abs(a.Width - b.Width), Math.Abs(a.Height - b.Height)));
    private static string Fmt(Box b) => $"{b.X:F1},{b.Y:F1} {b.Width:F1}x{b.Height:F1}";
}
