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
| `src/Pubsmith.Rendering` | SkiaSharp renderer. One drawing path for PNG and PDF. Also font resolution with substitution warnings, and the image comparer used for fidelity tests. A `RenderContext` reads each picture once, keeps decoded copies within a budget, and owns them; it keeps what it first found for each picture file (only a file it could not read is tried again), so use one per command, on one thread, and dispose it |
| `src/Pubsmith.Cli` | The `pubsmith` command: `render` (a `.pub` or a document JSON to PDF/PNG) and `import` (a `.pub` to a document JSON) |
| `tests/` | xUnit tests. `tests/fixtures/pub` holds the Publisher-built feature files with Publisher's own layout export and renders of them, which are the expected results |
| `tools/oracle` | Publisher automation: the harvest export, the feature and diff corpus builders, the layout export, and the PubOracle VM inbox scripts |
| `docs/plan` | Work-item plans (acceptance criteria → tests → results) |
| `docs/format/pub-format-notes.md` | What we know about the `.pub` format, in our own words |
| `docs/publisher-oracle-vm.md` | Setting up the Publisher 2021 test VM |

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
