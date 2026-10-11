# Pubsmith

Pubsmith opens Microsoft Publisher `.pub` files without Publisher, for people left with files they can no
longer open after Publisher's retirement (October 2026). It reads the file natively (no Publisher, no
LibreOffice), turns it into an open JSON document format, and renders it to PNG and vector PDF.

## Rescue a publication

```bash
pubsmith render brochure.pub --pdf brochure.pdf
```

```bash
pubsmith import brochure.pub --out brochure-rescued
```

`render` converts straight to PDF and/or PNG (`--png page.png --dpi 300`; a multi-page publication gives
`page-p1.png`, `page-p2.png`, ...). `import` writes the editable document (`brochure.json`), its pictures, and
`brochure.import.json`, which lists everything drawn approximately, shown as a placeholder outline, or left out.
Nothing is dropped silently: what Pubsmith can't reproduce yet is outlined on the page and named in the report,
and a damaged file is reported as such instead of crashing the tool.

Until there are releases, build it yourself (below) and run `dotnet src/Pubsmith.Cli/bin/Release/net10.0/pubsmith.dll`
in place of `pubsmith`.

**Status:** early. The native reader decodes page structure, geometry, fills and lines, pictures, WordArt and
text of Publisher 2003–2021 files. On 142 single-feature files built by Publisher itself, every editable shape
and text box matches Publisher's own layout export (frame within 0.1 pt, rotation, fill and text); in 41 of them
Publisher's export turns every element into a picture, so for those only page count and size are compared.
Whole real pages are still being brought up to Publisher's look: on the maintainers' own 106-page corpus, 18 %
of pages are within the target score (`docs/plan/WI-004-native-pub-reader-spike.md`). Tables, freeform shapes,
gradients, picture adjustments and linked text boxes are not reproduced yet. Files from Publisher versions
before 2003 are not supported. There's no editor UI yet.

| Project | What |
|---|---|
| `src/Pubsmith.PubReader` | Native `.pub` reader: compound file, OfficeArt shapes, Publisher's Contents and Quill (text) streams, and the importer that maps them onto the document model |
| `src/Pubsmith.Core` | Document model (pages, shapes, images, text), JSON format with schema versioning |
| `src/Pubsmith.Rendering` | SkiaSharp renderer. One drawing path for PNG and PDF. Also font resolution with substitution warnings, and the image comparer used for fidelity tests. A `RenderContext` reads each picture once, keeps decoded copies within a budget, and owns them; it keeps what it first found for each picture file (only a file it could not read is tried again), so use one per command, on one thread, and dispose it. It also carries the limits on rendering (below) |
| `src/Pubsmith.Cli` | The `pubsmith` command: `render` (a `.pub` or a document JSON to PDF/PNG) and `import` (a `.pub` to a document JSON) |
| `tests/` | xUnit tests. `tests/fixtures/pub` holds the Publisher-built feature files with Publisher's own layout export and renders of them, which are the expected results |
| `tools/oracle` | Publisher automation: the harvest export, the feature and diff corpus builders, the layout export, and the PubOracle VM inbox scripts |
| `docs/plan` | Work-item plans (acceptance criteria → tests → results) |
| `docs/format/pub-format-notes.md` | What we know about the `.pub` format, in our own words |
| `docs/publisher-oracle-vm.md` | Setting up the Publisher 2021 test VM |

## Limits on rendering

A document can come from anywhere, so rendering one is bounded. Each `RenderContext` carries `RenderLimits`; the
defaults sit far above ordinary publications: the real publications, WI-004's 1,000-page books and a catalogue of 100
photos use at most half of each (`docs/plan/WI-006-render-limits-hostile.md`).

| Limit | Default | Past it |
|---|---|---|
| `MaxPages` | 20,000 pages per context, PDF and PNG together | refused before anything is written |
| `MaxPngPixels` | 20 gigapixels per context | refused before anything is written |
| `MaxPdfTypefaces` | 36 distinct typefaces in one PDF's text boxes (WordArt is drawn as paths and not counted) | refused before anything is written |
| `MaxFontNames` | 4,096 distinct font names per context (cut to 256 characters, ignoring case) | refused before the element that names one more |
| `MaxTextBoxCharacters`, `MaxTextBoxRuns` | 8,388,608 characters and 262,144 runs in one text element, WordArt included (a paragraph without runs counts as one run) | refused before the element is laid out |
| `MaxWordArtGlyphs` | 16,384 glyphs in one WordArt | refused before it is built |
| `MaxDrawTime` | 600 s of drawing per context | stopped at the next checkpoint |
| `MaxPdfBytes` | 2 GiB in one PDF | stopped, and the PDF aborted |
| `MaxPngBytes` | 16 GiB of encoded PNG per context | refused before the page is written |
| `MaxPdfLayerPixels` | 64 megapixels for one shadow's layer in a PDF | refused before the layer is made |
| `MaxPdfPicturePixels` | 64 megapixels for a picture that counts as decoded in a PDF | a placeholder in that PDF (`image-too-large`) |
| `MaxPictureFileBytes` | 256 MiB for a picture file | not read: a placeholder (`image-too-large`) |

A refusal or a stop throws `RenderLimitException`: `Limit` is one of its constants, `Value` the amount, and `Page`
and `Element` count from 1 when they are known. Every later render with that context throws it again (a refusal
by `RenderLimits.Check` does not mark the context: the check is pure).
`RenderLimits.Check(document, outputs, context)` refuses a whole command before anything is written, as the CLI does,
and charges nothing. A library caller can pass other limits to `new RenderContext(baseDirectory, limits)`; the CLI
has no option to raise them.

- **Drawing time is wall-clock time** on the machine that renders, measured inside `PageRenderer.Render` (not PNG
  encoding, a PDF page's end or its close). It includes waits for disk and memory, so a refusal near the limit is not
  reproducible.
- **On your own canvas**, `PageRenderer.Render` meters drawing and applies the per-element limits, and after a stop
  it restores the canvas to the save count it had. The PDF guards (the picture and layer caps, the PDF's bytes) apply
  only to `Exporter.ToPdf`.
- **Pictures in a PDF.** A JPEG goes into a PDF whole when it is in one well-formed shape: baseline, extended or
  progressive Huffman coding, 8 bits, grayscale or YCbCr, top-left, uncropped and not shadowed. Pubsmith counts any
  other picture as decoded, so one of more than 64 megapixels is a placeholder in the PDF: cropped, turned or
  mirrored, shadowed, CMYK, stored as RGB, lossless, arithmetic-coded, 12-bit or otherwise outside that shape, or not
  a JPEG. The same picture is drawn in PNG output.
- **Bitmaps.** One page's bitmap is at most `Exporter.MaxPixels` (500 megapixels): a Letter page renders up to about
  2,310 dpi.
- **Documents.** `DocumentJson.Load` refuses a file of more than `MaxLoadBytes` (256 MiB, the most an import writes).
- **The CLI** checks the whole command, writes each output to `<output>.<guid>.tmp`, then replaces the outputs
  together, keeping each earlier output as `<output>.<guid>.backup` until all are in place. A refused, stopped or
  failed command leaves every earlier output as it was, or names in its error what it could not put back. A run
  never touches files with these names left by a run that was killed: a `.tmp` file can be deleted; a `.backup` file
  holds its output as it was before that run, so if the output is missing, rename the backup back to it.
- **Not bounded yet (WI-007).** A PDF page's end: the PDF writer holds a page's whole content until the page ends,
  then writes it at once, so one hostile PDF page can still exhaust memory before `MaxDrawTime` stops it. A fault in
  your output stream while a picture is written can still hang the process. A PDF's close, which writes each
  typeface's glyphs: its time grows faster than the distinct glyphs drawn from a typeface, and it is bounded only
  because `MaxDrawTime` limits the glyphs drawn (10,000 distinct ideographs of one typeface took about a minute to
  close). Total memory.

## Build and test

Requires the .NET 10 SDK.

```bash
dotnet build -c Release
```

```bash
dotnet test
```

Coverage (house floor: 85 % line coverage per touched file):

```bash
dotnet test --collect:"XPlat Code Coverage"
```

The opt-in calibration suite (minutes long; run it on a quiet machine) measures ordinary drawing times and stops
each hostile class; run its 225 MP JPEG check on its own, since it watches the process's peak memory:

```bash
PUBSMITH_CALIBRATE=1 dotnet test tests/Pubsmith.Rendering.Tests --filter "FullyQualifiedName~CalibrationTests&FullyQualifiedName!~APassedThroughJpegAt225MP"
```

```bash
PUBSMITH_CALIBRATE=1 dotnet test tests/Pubsmith.Rendering.Tests --filter "FullyQualifiedName~APassedThroughJpegAt225MP"
```

**Platforms.** Developed and tested on Windows. SkiaSharp ships native libraries for Windows and macOS; Linux
needs the `SkiaSharp.NativeAssets.Linux` package, which is not referenced yet. The rendering tests assume
Windows fonts (Arial, Arial Black, Calibri, Segoe UI, Times New Roman, Courier New).

**Tools.** The scripts in `tools/oracle` need PowerShell 7 and a working Publisher (a desktop install that
still runs, or the PubOracle VM). The fidelity scoring scripts also need ImageMagick (`magick`), and
`matte.py` needs Python 3 with numpy and Pillow.

## Reference material and the corpus suite

Everything the always-on tests need is in `tests/fixtures/`: Publisher-built single-feature `.pub` files, Publisher's
own layout export and 300 dpi renders of them, and neutral test art. The opt-in corpus suite measures real-world
fidelity on real `.pub` files: harvest them with `tools/oracle/Export-PubHarvest.ps1` and
`tools/oracle/Invoke-PubLayoutBatch.ps1` (both need a working Publisher; the layout exports go to the harvest's
`funcular\` folder), then set `PUBSMITH_CORPUS` to that folder. Real files are never committed. The suite is
calibrated to the maintainers' own 36-file corpus (its file count and known text mismatches), so on another
corpus read its report rather than its pass/fail; it writes `native\` and `native-report.csv` into that folder.

## Not affiliated with Microsoft

Pubsmith is an independent project. Microsoft and Publisher are trademarks of Microsoft Corporation. The file-format
knowledge comes from Microsoft's published OfficeArt specification (MS-ODRAW), from our own differential tests, and
from notes written in our own words after studying libmspub (MPL-2.0); see `docs/format/pub-format-notes.md`.

## License

MIT; see `LICENSE`. Third-party components and their licences are listed in `THIRD-PARTY-NOTICES.md`.
