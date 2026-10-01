using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pubsmith.Core;

/// <summary>An sRGB colour with alpha. Serialised as "#RRGGBB", or "#RRGGBBAA" when not opaque.</summary>
[JsonConverter(typeof(RgbaJsonConverter))]
public readonly record struct Rgba(byte R, byte G, byte B, byte A = 255)
{
    public override string ToString() => A == 255
        ? string.Create(CultureInfo.InvariantCulture, $"#{R:X2}{G:X2}{B:X2}")
        : string.Create(CultureInfo.InvariantCulture, $"#{R:X2}{G:X2}{B:X2}{A:X2}");

    public static Rgba Parse(string text)
    {
        if (text is null || text.Length is not (7 or 9) || text[0] != '#')
            throw new FormatException($"Colour '{text}' is not #RRGGBB or #RRGGBBAA.");
        if (!uint.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v))
            throw new FormatException($"Colour '{text}' contains non-hex digits.");
        return text.Length == 7
            ? new Rgba((byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : new Rgba((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }
}

internal sealed class RgbaJsonConverter : JsonConverter<Rgba>
{
    public override Rgba Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Colour must be a string like \"#RRGGBB\".");
        try { return Rgba.Parse(reader.GetString()!); }
        catch (FormatException ex) { throw new JsonException(ex.Message, ex); }
    }

    public override void Write(Utf8JsonWriter writer, Rgba value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}
