namespace Pubsmith.PubReader.Tests;

internal static class Fixtures
{
    public static string Dir { get; } = Path.Combine(AppContext.BaseDirectory, "fixtures", "pub");

    /// <summary>Diff-corpus variant, e.g. Pub("geometry", "base").</summary>
    public static string Pub(string group, string variant) => Path.Combine(Dir, $"{group}__{variant}.pub");

    public const double Emu = 12700;

    /// <summary>EMU edge values Publisher stores for a frame: relative to the page centre.</summary>
    public static (int L, int T, int R, int B) Edges(double x, double y, double w, double h, double pageW = 612, double pageH = 792) =>
        ((int)Math.Round((x - pageW / 2) * Emu), (int)Math.Round((y - pageH / 2) * Emu),
         (int)Math.Round((x + w - pageW / 2) * Emu), (int)Math.Round((y + h - pageH / 2) * Emu));
}
