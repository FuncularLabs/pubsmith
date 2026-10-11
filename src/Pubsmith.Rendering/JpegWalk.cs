namespace Pubsmith.Rendering;

/// <summary>
/// The one JPEG shape a PDF passes through (WI-006 AC4); every other picture counts as decoded. Narrower than what SkPDF
/// passes through, so it errs towards counting a picture as decoded.
/// </summary>
internal static class JpegWalk
{
    /// <summary>
    /// True when the file's markers, after the SOI and up to the first SOS, are each exactly one 0xFF byte and one of
    /// SOF0, SOF1 or SOF2 (just one, of precision 8 and 1 or 3 components), DHT, DQT, DRI, APP0 to APP15 or COM, each
    /// with a big-endian length that counts itself and stays inside the file; and libjpeg's colour rule makes the
    /// picture grayscale or YCbCr, not RGB: 1 component is grayscale; with 3, a JFIF marker makes it YCbCr; otherwise
    /// the last Adobe marker makes it RGB for transform 0 and YCbCr for any other; otherwise component IDs R, G, B make
    /// it RGB and any others YCbCr.
    /// </summary>
    internal static bool IsPassedThroughShape(ReadOnlySpan<byte> file)
    {
        if (file.Length < 2 || file[0] != 0xFF || file[1] != 0xD8) return false;
        var sofs = 0;
        var components = 0;
        var ids = ReadOnlySpan<byte>.Empty;
        var jfif = false;
        int? adobeTransform = null;
        var i = 2;
        while (true)
        {
            if (i + 4 > file.Length || file[i] != 0xFF) return false;
            var code = file[i + 1];
            if (code == 0xDA) break;   // the first SOS ends the walk
            var known = code is 0xC0 or 0xC1 or 0xC2 or 0xC4 or 0xDB or 0xDD or 0xFE or (>= 0xE0 and <= 0xEF);
            if (!known) return false;  // another SOF, a TEM or RST, a stuffed 0x00, a fill byte, DAC, DNL, anything else
            var length = (file[i + 2] << 8) | file[i + 3];
            if (length < 2 || i + 2 + length > file.Length) return false;
            var data = file.Slice(i + 4, length - 2);
            switch (code)
            {
                case 0xC0 or 0xC1 or 0xC2:
                    if (++sofs > 1 || data.Length < 6 || data[0] != 8) return false;
                    components = data[5];
                    if (components is not (1 or 3) || data.Length < 6 + 3 * components) return false;
                    ids = data.Slice(6, 3 * components);
                    break;
                case 0xE0 when data.Length >= 14 && data[..5].SequenceEqual("JFIF\0"u8):
                    jfif = true;
                    break;
                case 0xEE when data.Length >= 12 && data[..5].SequenceEqual("Adobe"u8):
                    adobeTransform = data[11];
                    break;
            }
            i += 2 + length;
        }
        if (sofs != 1) return false;
        if (components == 1 || jfif) return true;
        if (adobeTransform is { } transform) return transform != 0;
        return !(ids[0] == 0x52 && ids[3] == 0x47 && ids[6] == 0x42);
    }
}
