# WI-006: Rendering is stopped by a time limit and refused by counted limits; no document can run unbounded

**Goal:** a document that stays inside every import limit (or a hand-written `doc.json`) either renders within a
stated drawing time and output size, or is refused by name: at once, before any output is written, when a counted
limit says so; otherwise when its drawing time passes the limit. Sizes that would make one allocation too large are
refused before they are allocated. Ordinary publications, including WI-004's large ones, never meet a limit. A PDF
page's end (SkPDF writes the page's whole buffered content at once), fault containment for the caller's stream and the
full memory bound are WI-007.

**Why now:** WI-005 removed the measured hot spots of ordinary documents. What is left is what a hostile document can
multiply: single classes turn a small file into hours of rendering, tens of gigabytes of output, or gigabytes of
memory (Measurements).

**Revisions.**
- Rev 1 bounded time with an estimate made before drawing. Task 0 round 1 measured seven costs it missed or
  under-charged and per-class timings that moved 2× between quiet runs: no enumerated estimate can be shown to be an
  upper bound.
- Rev 2 replaced the estimate with a meter: drawing time measured at the renderer's one chokepoint, the render
  stopped when it passes the limit; counted limits for what the document states exactly; crash guards.
- **Rev 3** answers Task 0 round 2: a text line drawn as one call escaped the meter (now drawn in runs of 64 glyphs
  or fewer, each after a checkpoint); a stopped PDF was still closed by disposal (now aborted); the PDF layer guard
  needed glyph paths (now checked at draw time); how the CLI's count and the exporters' counts relate is now defined;
  the defaults are restated against plausible ordinary inputs (a linked story in one box, a passed-through JPEG,
  typefaces counted rather than their file bytes); the PDF byte limit is back (a count checked at each checkpoint;
  only fault containment stays in WI-007); the CLI's commit is safe on the next run.
- **Rev 4** answers Task 0 round 3: a PDF page's end, which writes the page's whole buffered content at once, is now
  bounded by a per-page content count checked before it (AC6); the chunked-text construction is written out exactly,
  with rows that fail against the variants that change pixels; CMYK and mirrored JPEGs count as decoded in a PDF; the
  PDF metering test uses empty pages; a stop on a caller's own canvas restores it; the CLI's commit deletes its
  backups on success; the font pre-pass stops at the name limit before resolving; empty paragraphs and WordArt runs
  get checkpoints.
- **Rev 5** answers Task 0 round 4, whose High finding (U1) was the second in a row on the PDF page's end: so AC6's
  units were redesigned from data before rev 5 was written. Every place the renderer draws on a PDF page was measured
  for what SkPDF holds (Measurements); the page content count now charges each draw site its measured upper bound,
  per primitive or per point of a path the renderer builds, skips text lines SkPDF culls, and a test asserts for every
  draw site that the count is at least what SkPDF holds. JPEG pass-through follows libjpeg's YCbCr-or-grayscale rule
  (a JPEG stored as RGB is decoded).
- **Rev 6** answers Task 0 round 5. Its High finding (V2) was the third in a row on the PDF page's end: each count of
  what SkPDF holds was beaten by another of SkPDF's internals (long numbers, colours, Type3 fonts, culling by device
  bounds under rotation). The owner moved that bound to WI-007, with all its data and findings, so WI-006 no longer
  counts page content; AC6 states the gap and what it measured. The JPEG rule followed libjpeg's, marker by marker (rev 8 makes it a
  whitelist).
- **Rev 7** answers Task 0 round 6: AC4's JPEG walk is stated byte by byte, stricter than libjpeg where that errs
  towards decoding, and its test takes SkPDF's own verdict as the oracle for every row; the calibration row records
  the page-end class instead of asserting a bound; the close's payload and the WI-007 stub are completed.
- **Rev 8** answers Task 0 round 7, the fourth round in a row on the JPEG rule (lossless JPEGs and a stuffed byte
  passed a walk that described what SkPDF decodes). So the rule is inverted: a JPEG is passed through only in one
  narrow, well-formed shape, and everything else counts as decoded; and a generated test, seeded variants of small
  JPEGs with SkPDF as the oracle, checks that nothing the rule passes is ever decoded.
- **Rev 9:** Task 0 round 8 approved rev 8 (the whitelist held against 117 crafted files and 100,000 generated
  variants, and passed 108 and 200 MP phone-style photos through); its six non-blocking items are folded into the
  wording here. Rev 10 applies the verification of rev 9 (Z1 to Z6, none blocking); rev 11 applies the
  verification of rev 10 (A1 to A4, nits).
  Each finding of the eight rounds is answered in the tables at the end.

## Measurements (2026-10-01, before this work)

**Conditions.** One development machine (Windows 11 x64, 24 logical cores, 64 GB), Release build. Per-class times
varied up to 4× between runs for one class (about 2× for most), including at 0–4 % CPU load, so the figures below are
indicative. Sources: the rev-1 probe (run while another session ran tests), the probes of Task 0 rounds 1 to 4
(in-run ratios) and the content probe below; where they differ the range is given. Their outputs are kept with the review notes, outside the
repository.

Per unit, PNG at 150 dpi / PNG at 300 dpi / PDF:

| What a document repeats | Measured |
|---|---|
| A page area of a picture drawn (1 MP source; 64 MP source) | 40–86 / 161–366 ms; 96 / 349 ms; under 1 ms in PDF |
| A page area of a layered shadow (a translucent shape's) | 11.8–25 / 48–96 / 7.9–14 ms |
| A 1 × 1 pt shape with a translucent 1,000 pt line; the same, shadowed | 1.9 / 11 / 0.002 ms; 21 / 80 / 13.5 ms |
| A WordArt glyph: with a 1,000 pt outline; one of 300 in a page-sized frame | 0.59 / 3.5 / 0.09 ms; 0.60 / 1.5 / 0.14 ms |
| A text glyph: zero-advance marks stacked in one line (20,000 took 44.6 s at 300 dpi) | 2.2 ms each at 300 dpi |
| A PDF page's end: SkPDF holds the page's content until `EndPage`, then compresses and writes it at once | 2.6–4.6× the page's drawing time; about 31 B per small rectangle and 750 B per WordArt glyph written, about 3 KB of memory per WordArt glyph (80 WordArt of 1,024 glyphs: drawing 1.08 s, end 4.99 s, 61.4 MB) |
| A CMYK JPEG in a PDF (SkPDF decodes it; a YCbCr or grayscale one is passed through) | 48 MP: 3.1 s, 704 MB peak, 140 MB written in one step (the YCbCr one: 22 ms, 4.95 MB) |
| A distinct crop in a PDF (a 1 % crop of a 4 MP PNG) | 29 ms and 7 KB: SkPDF decodes the whole source for each crop |
| Two pictures larger than the decode cache together, placed alternately | a full decode at every placement (two 56 MP, 10 placements: 2.6 s, against 0.5 s in order; PNG at 150 dpi) |
| A distinct installed typeface in a PDF (Latin; CJK) | 157–160 ms and 2.2 MB; 354–747 ms and 4.5–13.3 MB (fonts are written at `Close`; 951 Latin faces: 39.4 s) |
| A page (empty) | 104 / 466 / 0.014 ms: PNG encoding, about 50 ns per output pixel |
| A text character (laid out, visible or clipped) | 0.4–1.6 / 3.0–5.7 / 0.8 µs |

What that allows inside the import limits (estimated from the table): 249,000 shadowed 1 × 1 pt shapes with
1,000 pt lines, about 5.5 hours to PNG at 300 dpi; one picture page-sized 249,000 times, about 25 hours at 300 dpi;
249,000 alternating placements of two pictures, about 17 hours at 150 dpi; one picture with 249,000 distinct crops,
about 2 hours to PDF; one text line of stacked marks, about 37 minutes per million glyphs; one PDF page that draws
for 100 s, about 7.7 minutes to end, writing about 5.7 GB at once and using about 23 GB of memory. Memory: a 20,000 pt PDF
page with one page-sized layered shadow peaked at 4.6 GB (imports allow 34,560 pt); 400 typefaces in one PDF peaked
at 3.9 GB or more; loading a 256 MiB `doc.json` peaked at 2.3 GB. A hand-written `doc.json` has no size limit today.

**What SkPDF holds per drawing** (rev 5's content probe: one-page PDFs of 0 and of 50 or 200 instances of each
drawing; the page's content stream inflated; bytes per instance; the starting data for WI-007's page-end bound, which
Task 0 round 5 showed is not an upper bound: long numbers, a colour per instance, Type3 fonts, conic points,
rotated text culled by device bounds, ink outside a line's box and silhouettes drawn without a path all hold more):

| One instance of | Bytes held in the page's content stream |
|---|---|
| A rectangle, any size (25 pt to 60,000 pt); rotated | 19–31 B; 121 B |
| An ellipse (grows with size, then stops: 25, 600, 6,000, 60,000 pt) | 1.0, 4.0, 7.9, 7.9 KB |
| An ellipse with a line (300 pt; line 1 to 4,000 pt) | 8.1 KB |
| An ellipse with a line and its silhouette shadow (300 pt; line 1; line 4,000 pt) | 9.1 KB; 17.1 KB |
| A picture; cropped; the missing-picture placeholder with a line | 60 B; 60 B; 161 B |
| An oval-masked picture (25, 600, 6,000 pt; with a line, 300 pt) | 0.6, 2.1, 8.0 KB; 6.1 KB |
| A layered shadow, on the page (its content is drawn into the layer) | about 40–60 B more than the element alone (a rectangle; a rotated ellipse) |
| A text box of one run of 20 glyphs; of 20 runs of one glyph; whose line lies below its frame | 183 B; 1.7 KB; 0 B (culled) |
| A WordArt glyph ("W", envelope): fill; fill and outline; a complex glyph ("&") with an outline | 1.1 KB; 2.2 KB; 16.7 KB |

**Ordinary** (drawing time only, Task 0 round 1): a 100-page catalogue of distinct 14.4 MP photos, 9.8 / 26.8 /
0.8 s; a 1,000-page book of 6,000 characters a page, 4.2 / 5.6 / 9.6 s, and about 430 s at 300 dpi with its PNG
encoding (8.4 gigapixels). WI-004's 300 pages of one linked story place that story in one text box (450,000
characters). **Real files** (192: 36 real publications and the test files), the largest of each, per file: 11
pages; 444 elements; 22,944 characters; 776 WordArt glyphs; 9 distinct pictures (21 MP); 9 resolved typefaces (15
family/bold/italic combinations, 8 names); layered shadows covering 0.58 Letter pages (0.28 MP at 1 px/pt); drawing
at 300 dpi about 6 s for the slowest.

## Acceptance criteria

| AC | Criterion |
|---|---|
| AC1 | **Counted limits, before anything is written.** Known exactly from the document and the outputs asked for, and checked before drawing: pages written, PDF and PNG together (`MaxPages`, default 20,000 per context); PNG output pixels (`MaxPngPixels`, 20 gigapixels per context); distinct resolved typefaces of one PDF's text boxes (`MaxPdfTypefaces`, 36; WordArt is drawn as paths, so its fonts are not embedded and not counted). A refusal throws `RenderLimitException` (`Limit`, `Value`, `Page` when known, `Element` when known) before anything is written: by `Exporter` for its own call, and by the CLI for the whole command through the public, pure `RenderLimits.Check(document, outputs, context)`, which computes exactly what the exporters will add (Design). Before it resolves typefaces, the check cuts names to 256 characters, de-duplicates them and stops at `MaxFontNames` + 1 distinct names (AC2's refusal), so it resolves at most 4,096 names (16,384 resolutions: each name in up to four styles). A context's counters only grow, and after any `RenderLimitException` every later render with it throws before writing. |
| AC2 | **Per-element limits, wherever the page is drawn.** `PageRenderer.Render` (the exporters, and a caller's own canvas) refuses, before drawing the element, a text box of more than `MaxTextBoxCharacters` (8,388,608, the most text an import places in all) or more than `MaxTextBoxRuns` (262,144: above the 250,000 units that bound the runs an import places), and a WordArt of more than `MaxWordArtGlyphs` (16,384) glyphs; and refuses a document naming more than `MaxFontNames` (4,096) distinct font names in one context, counted where fonts are resolved. These text limits apply to every text element, WordArt included. Font names longer than 256 characters are cut to 256 before they are resolved, as the importer cuts them. |
| AC3 | **A drawing-time limit.** Every render through `PageRenderer.Render` adds the time spent drawing to its context, from the context's `TimeProvider` (the system's by default), in a `finally` (a render that throws still adds its time); when the total passes `MaxDrawTime` (default 600 s, and at least ten times the slowest ordinary drawing time Task 1 measures) the render stops at the next checkpoint with `RenderLimitException`. Checkpoints: before each element; between a shadow and its element; before each paragraph and each run is laid out; before each text line is drawn and before each run of at most 64 glyphs of a piece (1 glyph when the font is more than 256 device pixels tall: its size times the square root of the absolute determinant of the canvas's total matrix, computed directly; `SilhouetteScale` is not used, since it answers 300/72 for a PDF) is drawn, by the construction in Design, which is pixel-identical to drawing the piece at once; before each WordArt run and glyph is built and before each WordArt glyph is painted. (A picture needs no checkpoint of its own: its decode and draw follow the element's or the shadow's checkpoint directly.) On a caller's own canvas, a stop restores the canvas to the save count it had on entry (no layer left open, no rotation or offset left); the exporters' canvases are discarded or aborted instead. PNG encoding, a PDF page's end and a PDF's close are not drawing time (AC1 and AC6 bound encoding and the close; a page's end is WI-007's). `MaxDrawTime` is wall-clock time on the machine that renders: it includes waits for disk and memory, so a refusal near the limit is not reproducible (the README says so); ordinary documents sit far below it (AC5). |
| AC4 | **Crash guards.** In a PDF made by `ToPdf`: a picture that counts as decoded, that is anything but a JPEG in the one passed-through shape described below, with EXIF origin TopLeft (a mirror counts as turned), uncropped and drawn outside a shadow layer, of more than `MaxPdfPicturePixels` (64 MP), is the placeholder with `image-too-large`, a verdict for that PDF only (the same picture draws in a bitmap, in either order with one context). A JPEG is passed through only in one shape, and every other picture counts as decoded. Its markers, read from the bytes the cache has already read: after the SOI, each marker is exactly one 0xFF byte followed by one of these codes, each with a two-byte big-endian length that counts itself and stays inside the file, up to the first SOS: one SOF0, SOF1 or SOF2 (baseline, extended or progressive Huffman coding) of precision 8 and 1 or 3 components; DHT; DQT; DRI; APP0 to APP15; COM. Anything else where a marker is expected (another SOF, such as lossless or arithmetic coding; a second SOF; a stuffed 0x00; a fill byte; a TEM or RST; any other code) makes the picture count as decoded. A JFIF marker is an APP0 whose data after the length is at least 14 bytes and starts with the five bytes `J F I F 0x00`; an Adobe marker is an APP14 whose data after the length is at least 12 bytes and starts with the five bytes `A d o b e`, its transform being data byte 11, counting from 0. libjpeg's colour rule then decides: 1 component is grayscale; with 3, a JFIF marker makes it YCbCr; otherwise the last Adobe marker makes it RGB for transform 0 and YCbCr for any other; otherwise component IDs R, G, B (0x52, 0x47, 0x42) make it RGB and any others YCbCr. Only a grayscale or YCbCr JPEG is passed through. (Every picture SkPDF decodes counts as decoded here; SkPDF also passes through some files the shape excludes, which therefore cost a placeholder only when over 64 MP.) In a PDF, too, a layered shadow whose layer, at the PDF's 1 pixel per point, under the element's rotation and the shadow's offset, clipped to the page and rounded out as SkPDF allocates it, would exceed `MaxPdfLayerPixels` (64 MP), or is not finite, is refused before the layer is made: `RenderLimitException`, sticky like every other. In every output: a picture file larger than `MaxPictureFileBytes` (256 MiB; an import can write a larger one, up to its picture budget, from an uncompressed picture; Task 1 records the largest real picture file) is not read: placeholder and `image-too-large`. `FontResolver` caches at most 4,096 (name, bold, italic) keys, its own limit (so sharing it between contexts cannot grow it); past it, a font resolves as before, uncached. `Exporter.MaxPixels` becomes 500,000,000 (one page's bitmap must stay under SkiaSharp's 2 GiB allocation: a Letter page at 2,400 dpi, 538.6 MP, failed to allocate); `DocumentJson.Load` refuses a file of more than `MaxLoadBytes` (256 MiB) by reading through a limited stream, with its own message naming the file; the CLI prints it and exits 1. |
| AC5 | **Ordinary documents never meet a limit.** The 192 real files, WI-004's three ordinary large publications (a 1,000-page book with a master; 300 pages of one linked story; 100 pages of 200 shapes) and a 100-page catalogue of distinct 14.4 MP photos, in PDF and in PNG at 300 dpi: every counted value at most half its limit (typefaces at most a quarter), and every drawing time at most a tenth of `MaxDrawTime` on the reference machine (Task 1 measures them before the defaults are fixed). |
| AC6 | **Output is bounded, and the worst case is stated, gap included.** PDF bytes: `ToPdf` writes through a counting stream (its `Position` is its own count, so a stream that cannot seek works; today it crashes the process) and checks the count at each checkpoint, after each page's end and before its close; past `MaxPdfBytes` (2 GiB) it aborts the document and throws; the bound is the limit plus one step's writes (one image: a decoded picture or a layer's image of at most 64 MP, with its gray soft mask when it has transparency, or a passed-through JPEG of at most `MaxPictureFileBytes`), plus one page's end, plus the close's payload (at most `MaxPdfTypefaces` fonts, measured up to 13.3 MB and 0.75 s each, and the document's dictionaries and cross-reference table: about 230 B a page, 48 B a page for each image the page uses, 34–36 B for each distinct graphic state such as a line width, and about 55 B for each image or layer written; about 150 MB for a document at the load limit, extrapolated). **A PDF page's end is not bounded until WI-007:** SkPDF holds a page's whole content until the page ends, then writes it at once; Task 0 measured, per second of drawing, up to about 230 MB of memory, 57 MB written and an end of 4.6 times the drawing time (WordArt), and more memory for kinds round 5 found, so one hostile PDF page can still exhaust memory before `MaxDrawTime` stops it; the README says so. PNG bytes: `ToPng` counts each encoded page against `MaxPngBytes` (16 GiB per context) before writing it. A command's worst case, recorded in Results from Task 7's measurements: loading (`MaxLoadBytes`), the counted check (at most 16,384 font resolutions), drawing time `MaxDrawTime` plus the longest step of each kind (steps grow with the page's pixels up to `MaxPixels`), each PDF page's end (unbounded, as above) and the close, PNG encoding at `MaxPngPixels` at the measured rate, and the bytes above. Memory: each single allocation the renderer makes is bounded by AC4, `MaxPicturePixels` and `MaxPixels`; what SkPDF holds for a page, and the total, are not bounded until WI-007. A fault in the caller's stream while SkiaSharp writes a picture can still hang the process (the WI-005 F11 hang); WI-007 contains it, and the README says so until then. |
| AC7 | **A stop leaves nothing finished.** On any `RenderLimitException` (or any exception) inside `ToPdf`, the document is aborted, never closed: the stream holds no trailer and no fonts written after the stop. The CLI checks the whole command (AC1) before writing; refuses two outputs with the same path (compared as full paths, ignoring case on Windows) as a usage error; writes every output to a temporary file; then commits: each existing output is moved to a backup with a unique name, each temporary moved into place; if any move fails, the moves are undone in reverse order (what was moved in is deleted, every output put back from its backup); a backup whose restore fails is kept and named in the error; when every move succeeded, the command deletes its own backups (one it cannot delete is named in a warning). A failed command deletes its temporaries. Backups and temporaries left by an earlier run that was killed are never touched (the README names them: `<output>.<guid>.backup`, `<output>.<guid>.tmp`). A refused, stopped or failed command leaves every earlier output byte for byte as it was. |
| AC8 | **Injectable, pinned, deterministic.** `RenderLimits` is a public record carried by `RenderContext`, with public defaults pinned by one test; the CLI has an internal entry taking limits, a clock, the load limit and its file operations (move, delete), for its tests. Tests use small injected limits and fake clocks: one that advances a fixed amount at each reading, and one that jumps when the output stream is written, so whether encoding and closing are metered can fail a test (for PDF with empty pages, since SkPDF writes while it draws anything). Wall-clock measurement happens only in the opt-in calibration suite (`PUBSMITH_CALIBRATE`). |
| AC9 | **Nothing else changes.** Checkpoints, checks and chunked drawing change no pixel: every page of the 192 real files renders exactly as on `main` (score 0.000); a long word drawn in runs is identical to the same word drawn at once; the real-file gate stays at 19/106 pages ≤ 3.0, median 8.63, text 429/432; existing tests pass unchanged, except `LimitsTests.LetterAtMaxDpi_IsWithinTheCap` (a Letter page at 2,400 dpi is now over `MaxPixels`, as it could not be allocated; Letter renders up to about 2,310 dpi) and the `MaxPixels` comment. |

## Design

- **The meter** (`RenderMeter`, inside `RenderContext`): `PageRenderer.Render` starts it on entry (counting from the
  outermost start) and stops it in a `finally`; `ctx.Checkpoint(page, element)` reads the clock, adds the time, and
  throws past `MaxDrawTime`. `TextRenderer.Draw` and `PrepareStretched` have the context; `WordArt.Paint` and
  `StretchedText.Draw` are given it, for their per-glyph checkpoints. Diagnostics counters (internal, for tests):
  paragraphs and runs laid out, lines drawn, text runs of glyphs drawn, WordArt runs built, WordArt glyphs built and
  painted, pictures drawn. On a caller's canvas (no exporter mark) `Render` remembers the save count on entry and restores to it in its
  `finally`.
- **Chunked text drawing** (the one construction found pixel-identical to drawing the piece at once, in 198 of 198
  configurations): the piece's glyphs from `SKFont.GetGlyphs(text)`; their positions from
  `SKFont.GetGlyphPositions(glyphs, new SKPoint(0, 0))` for the whole piece, used unchanged; each run of glyphs built
  as a positioned text blob with those positions and drawn with `canvas.DrawText(blob, x, baseline, paint)` at the
  piece's own origin. Runs split between glyphs, so a surrogate pair is never split. Variants that change pixels and
  must fail its test: advancing each run by the measured width of the previous ones; adding the origin in C#; re-basing
  positions per run; passing the origin to `GetGlyphPositions`.
- **Counting** (`RenderLimits.Check(PubsmithDocument, RenderOutputs, RenderContext)`, public and pure; `RenderOutputs`
  is `(bool Pdf, double? PngDpi)`): pages = the document's pages for each output asked for; PNG pixels = the sum of
  `Exporter.PixelSize` over the pages; typefaces = the distinct resolved typefaces of the PDF's text (resolved through
  the context's `FontResolver`: no warning, nothing drawn); added to the context's current counters and compared with
  the limits. The exporters add the same quantities as they render: `RenderBitmap` a page and its pixels (`ToPng`
  calls it, then adds its encoded bytes), `ToPdf` its pages, after checking its typefaces. PNG bytes are known only
  after encoding, so their refusal can come in the middle of a command (the CLI's staging covers it).
- **PDF:** `ToPdf` checks the context's refusal state and its counted limits before `CreatePdf`; writes through a
  counting stream (counts and forwards; `Position` returns its count; it does not contain the caller's faults:
  WI-007); on any exception calls `Abort()` before disposal.
  The layer guard runs in `DrawShadow` before `SaveLayer`: the canvas's total matrix maps `LayerBounds` to device
  bounds, clipped to the device clip and rounded out; under the PDF mark only.
- **Pictures:** the cache's entry keeps the encoded format, the EXIF origin and, for a JPEG, whether AC4's walk finds
  it in AC4's one passed-through shape (an internal function of the file's bytes, tested on them directly by
  `JpegWalkTests`);
  `DrawImage` knows whether it draws into a shadow layer; under the PDF mark a picture that will be decoded and is
  over `MaxPdfPicturePixels` is the placeholder. The file size is checked before the file is read.
- **CLI:** `Run(args, stdout, stderr)` calls an internal `Run(args, stdout, stderr, RenderLimits, TimeProvider,
  long maxLoadBytes, ICliFiles files)`, where `ICliFiles` moves and deletes files; the CLI's tests see it
  (`InternalsVisibleTo`). Backups are named `<output>.<guid>.backup`.
- **Not changed:** drawing itself; `MaxDpi`; line spacing of 0 or less (its cost is drawing time). No CLI option raises
  a limit (a library caller can inject limits). A caller drawing on its own PDF canvas gets the meter and AC2, not the
  PDF guards of AC4 and AC6 (it is not marked as a PDF; the README says so). The README also lists which pictures
  over 64 MP become placeholders in a PDF (cropped, turned or mirrored, shadowed, CMYK, stored as RGB, lossless, arithmetic-coded, 12-bit or otherwise outside the passed-through shape, not JPEG) and
  the typeface limit.

## Test plan (tests before code; red first)

| AC | Test (project: Rendering unless noted) | Red today / mutation it must kill |
|---|---|---|
| AC1 | `CountedLimitTests.OverEachLimit_IsRefusedBeforeWriting` (theory: pages, PNG pixels, typefaces; injected limits; exception members; the output stream is empty) and `AtEachLimit_IsRendered` | red. Mutations: each check removed; `>=` for `>`; checked after drawing. |
| AC1 | `CountedLimitTests.TheCheck_MatchesWhatTheExportersAdd` (generated commands, PDF and/or PNG; the counted limits small, every other limit at its maximum: `Check` passes exactly when the exports finish without a counted refusal, one whose `Limit` is pages, PNG pixels or typefaces) and `TheCheck_ChargesNothing` | Mutations: `ToPng` and `RenderBitmap` both count; PDF pages not counted; the check charges. |
| AC1 | `CountedLimitTests.ThePrePass_StopsAtTheNameLimit_BeforeResolving` (a document of 10,000 distinct names, limit 100: refused with at most 101 resolutions) | Mutation: all names resolved first. |
| AC1 | `CountedLimitTests.ARefusedContext_RefusesEveryLaterRender` (before writing) | Mutation: not sticky. |
| AC2 | `ElementLimitTests.OverEachElementLimit_IsRefusedBeforeTheElement` (theory: characters, runs, WordArt glyphs, font names; through `ToPng`, `ToPdf` and `PageRenderer.Render` on a caller's canvas; the glyph-build and runs-laid-out counters stay 0 for the refused element) + `ALongFontName_IsCutTo256` | red. Mutations: limits checked only in `Exporter`; checked after layout; names not cut. |
| AC3 | `MeterTests.EachKindOfStep_IsStoppedInsideItsElement` (theory: many elements; a text box of many paragraphs, empty ones included; of many runs; many lines; one long line of stacked marks; one WordArt of many runs, empty ones included, and of many glyphs; many pictures; shadowed elements; fixed-step clock; the stop comes before the element's end by the diagnostics counters, WordArt runs and glyphs built included) | red. Mutations: each checkpoint removed (the picture one does not exist: its removal would be equivalent); a line drawn in one call; the time never compared. |
| AC3 | `MeterTests.EncodingPageEndAndClose_AreNotDrawingTime` (a clock that jumps an hour on each write to the output stream; PNG of a page without pictures; PDF of empty pages, since SkPDF writes while it draws anything: the metered total stays under a minute) | Mutation: meter around the whole export. |
| AC3 | `MeterTests.ARenderThatThrows_StillAddsItsTime`, `ACallersOwnCanvas_IsMetered_AndRestoredAfterAStop` (save count and matrix as on entry, inside a rotated, shadowed element), `TheTimeAddsUpAcrossCalls`, `AStoppedContext_StaysStopped` | Mutations: stop not in `finally`; meter only in `Exporter`; no restore on a caller's canvas; total reset per call. |
| AC3 | `ChunkedTextTests.TheOneGlyphRunThreshold_IsTheCanvasScale` (a PDF at 100 pt and at 300 pt: runs of 64 glyphs below 256 device pixels, of 1 above, by the text-runs counter) | Mutation: `SilhouetteScale` used. |
| AC3, AC9 | `ChunkedTextTests.ALongPiece_DrawnInRuns_IsPixelIdentical` (rows proven red before implementation against each variant named in Design: 1,000 random letters in Arial at 9.7 pt, 150 dpi, rotated 30°; 300 CJK ideographs with no space; plus 63, 64 and 65 glyphs, a surrogate pair at the boundary, a 300 pt font) | Mutations: the four variants in Design (each run against the rows, the runs recorded in the hand-off). |
| AC4 | `CrashGuardTests.APdfPictureThatWillBeDecoded_OverItsCap_IsAPlaceholder` (theory over small real JPEGs with crafted markers, built as rounds 6 and 7 built them or as each row states, each with two checks: the plan's verdict, placeholder or passed through, under an injected cap; and soundness against SkPDF itself: whenever the plan's verdict is passed through (the shape, the origin and the placement), the file's whole byte sequence appears in the PDF SkPDF writes from it uncapped. Rows (component IDs as stated): PNG; cropped, turned, mirrored, CMYK and shadowed JPEGs; lossless (SOF3) and arithmetic (SOF9, SOF11 headers) JPEGs with IDs 1, 2, 3 and no JFIF or Adobe marker; a 12-bit SOF1; two SOFs; a stuffed 0x00 before an Adobe 0 marker, sized as round 7's so that a walker reading a length after the stuffed byte lands on the next marker (IDs 1, 2, 3); Adobe transform 0 and 1; IDs 1, 2, 3 and R, G, B; JFIF; JFIF with Adobe 0; Adobe 2; Adobe 0 then Adobe 1; Adobe 1 then Adobe 0; Adobe 1 with IDs R, G, B; an Adobe 0 marker after the SOF (IDs 1, 2, 3); JFIF after the SOF; a JFIF marker shorter than 14 data bytes before an Adobe 0 marker; a JFIF marker of 13 data bytes (IDs R, G, B); an APP0 `JFIFX` (IDs R, G, B); Adobe markers of 10 and 11 data bytes (IDs R, G, B); an 8-byte Adobe marker (IDs R, G, B); Adobe with version 0x0164 and transform 0 (IDs 1, 2, 3); reordered (B, G, R) and unknown IDs; a TEM, an RST0 and a fill byte before an Adobe 0 marker sized as round 6's (IDs 1, 2, 3; APP14 lengths 65,516 and 61,165); a TEM before the tables; a DAC marker and a DNL marker before the SOS; 1 component with Adobe 0; and ordinary files (camera-style with EXIF and no JFIF, JFIF, grayscale, progressive, 4:4:4, ICC-tagged, Photoshop-style with Adobe 1), where the verdict must agree with SkPDF; PDF then bitmap and bitmap then PDF, one context) | red. Mutations: cap for bitmaps too; plain JPEGs capped; shadowed JPEGs passed; the colour condition dropped; "1 or 3 components" for libjpeg's rule; lossless or arithmetic SOFs allowed; precision ignored; a stuffed byte read with a length; markers read only before the SOF; the first Adobe marker kept; Adobe before JFIF; IDs before JFIF; JFIF matched on four bytes; Adobe matched with a NUL; lengths counted with the length field; short markers accepted; TEM, RST or a fill byte read with a length; DAC or DNL read with a length; verdict remembered in the cache. |
| AC4 | `JpegPassThroughSoundnessTests.GeneratedVariants_AreNeverPassedWhenSkPdfDecodes` (seeded and repeatable: 5,000 variants a run of small base files, made by inserting, removing, reordering and duplicating markers, stuffed and fill bytes (some followed by a length that skips the Adobe 0 or JFIF marker after them), changing SOF type, precision, component count and IDs, and JFIF and Adobe markers and their lengths; each rendered uncapped to a PDF: whenever the plan's verdict is passed through, the whole file appears in the PDF, and Skia's resource cache, purged before each render, does not grow while it is drawn; the class runs alone, in its own collection with parallel runs disabled; base files: a small JFIF JPEG, a camera-style JPEG with EXIF and no JFIF, a grayscale one, a progressive one and a Photoshop-style one with Adobe 1) + `JpegWalkTests` (the internal walk on crafted bytes that SKCodec does not read: truncated and overlong segments, two SOFs, no SOF) + opt-in `CalibrationTests.APassedThroughJpegAt225MP_IsNotDecoded` (in its own process, for each of round 8's passed placements: plain, explicit zero crop, rotated 30° and 90°, stroked, oval mask, tiny (1 pt) and page-sized: embedding takes under 1 s and peak memory grows by under 256 MB; round 8 measured at most 21 ms, against about 3 s for a decode, whose pixels alone take 900 MB) | Mutations the generated check can fail: lossless SOFs allowed, a stuffed byte read with a length, the colour rule widened to pass RGB (each is also killed by a crafted row, and the hand-off records each one's kill count over the seeded run); widenings SkPDF passes through anyway (precision, arithmetic SOFs, DAC and DNL markers) and narrowings of the colour rule's order (Adobe before JFIF, IDs before JFIF) are killed by `CrashGuardTests`' verdict rows instead; a second SOF allowed and no SOF passed are killed by `JpegWalkTests`. |
| AC4 | `CrashGuardTests.APdfLayerOverItsCap_IsRefusedBeforeTheLayer` (theory: rotated, offset, partly off the page, a path-warp WordArt, NaN and infinite frames; at the cap and one rounded-out pixel over; injected cap; the exception is sticky) | red. Mutations: rotation ignored; offset ignored; not clipped; not rounded out; NaN passed. |
| AC4 | `CrashGuardTests.APictureFileOverItsCap_IsNotRead` (reads counter 0), `FontResolver_CachesAtMost4096Keys_ThenResolvesUncached` (shared by two contexts), `MaxPixels_RefusesLetterAt2400Dpi_WithItsMessage` | red. Mutations: file read before the check; cache unbounded; old `MaxPixels`. |
| AC4 | Core `DocumentJsonTests.Load_PastItsLimit_IsRefused` (injected limit; at the limit it loads) + CLI `RenderLimitCliTests.ADocJsonTooLarge_IsNamed` | red. Mutations: size checked after reading; CLI message without the file. |
| AC5 | `OrdinaryDocumentTests.CountedValues_AreFarBelowTheirLimits` (WI-004's three publications and the catalogue, built in the test: each counted value against its fraction) + opt-in `CalibrationTests.OrdinaryDrawingTimes` and the real-file check | Mutation: a counted limit ÷ 10. |
| AC6 | `PdfLimitTests.ToPdf_ToAStreamThatCannotSeek_Completes` (in a child process until the counting stream exists, since today it ends the process) | red (process crash). Mutation: `Position` forwarded to the caller's stream. |
| AC6 | `PdfLimitTests.APdfOverItsBytes_IsAbortedWithinTheBound` (injected limit; crossed between elements, at a page end and at the close) + `CountedLimitTests` row for PNG bytes (refused before the page is written) + opt-in `CalibrationTests.EachHostileClass_IsStoppedOrRefusedWithinItsBound` (each class above and WI-004's hostile builder, the PDF classes built as many pages that each end within the machine's memory, so that the meter's stop comes before a page's end could exhaust it; the assertion is the metered drawing time against `MaxDrawTime`, not wall-clock time or peak memory, which include page ends AC6 leaves unbounded; the longest step of each kind; and, for the page-end class, recorded and not asserted: memory, bytes written, end time and peak, per second of drawing, on pages built to end within the machine) | red. Mutations: count checked only at close; PNG bytes counted after writing. |
| AC7 | `PdfLimitTests.AStoppedPdf_IsAborted_NotClosed` (no `%%EOF`, no font object after the stop; a stopped context's next `ToPdf` writes 0 bytes) | red. Mutation: no `Abort()`. |
| AC7 | CLI `RenderLimitCliTests.ARefusedCommand_WritesNothing`, `AStoppedCommand_WritesNothing` (injected clock), `TwoOutputsWithOnePath_IsAUsageError`, `AFailedReplacement_PutsEveryOutputBack` (three outputs; a move into place fails after another move into place succeeded, through the file-operation seam; the seam's calls show the undo in reverse order; no temporary remains), `ABackupThatCannotBeRestored_IsKeptAndNamed` (the seam fails one restore), `ASuccessfulCommand_LeavesNoBackupOrTemporary`, `ALeftoverBackup_SurvivesTheNextRun` | red. Mutations: PNG pages written as drawn; no backup; backups not restored; restored in forward order; duplicate paths accepted; backups never deleted. |
| AC8 | `CountedLimitTests.Defaults_ArePinned` | Mutation: any default changed. |
| AC9 | Real pages (maintainer's check, as WI-005's); opt-in gate; all suites | — |

Interface coverage (public, new or changed → tests): `RenderLimits` and each member, `RenderLimits.Check`,
`RenderOutputs` (`Pdf`, `PngDpi`) → `CountedLimitTests`, `ElementLimitTests`, `MeterTests`, `CrashGuardTests`,
`PdfLimitTests`; `RenderLimitException` (`Limit`, `Value`, `Page`, `Element`, message) → the same and CLI tests;
`RenderContext`'s constructor with limits and a `TimeProvider` → `MeterTests`; the `RenderLimitException.Limit`
values (string constants, one per limit, as `RenderWarning`'s codes are) → each limit's row; `Exporter.MaxPixels` →
`CrashGuardTests`; `DocumentJson.MaxLoadBytes`, `Load(path, maxBytes)` and the load exception's message →
`DocumentJsonTests`, CLI tests. Internal: the meter, its checkpoints and counters → `MeterTests`; chunked drawing →
`ChunkedTextTests`; the counting stream (`Position`) →
`PdfLimitTests`; the JPEG walk → `JpegWalkTests`, `JpegPassThroughSoundnessTests`, `CrashGuardTests`; the CLI's internal
`Run` and `ICliFiles` → CLI tests. Every touched file ≥ 85 % lines.

## Tasks (each names the tests it turns green)

0. **Plan review (Task 0):** rounds 1 to 7 AMEND; round 8 APPROVE (rev 8), with its non-blocking items folded into
   rev 9.
1. **Ordinary measurements first** (no new API needed): on the reference machine (its state recorded: power plan,
   nothing else running, best of 5), the drawing time of the real files, WI-004's three publications and the
   catalogue; the real files' typefaces, PNG and PDF bytes and largest picture file; then fix
   `MaxDrawTime` (ten times the slowest, rounded up to a minute, at least 600 s) and confirm every counted default
   against AC5.
2. Limits, exception, counted checks (`CountedLimitTests`).
3. Per-element limits (`ElementLimitTests`).
4. The meter, checkpoints and chunked text (`MeterTests`, `ChunkedTextTests`).
5. Crash guards and the load limit (`CrashGuardTests`, `JpegWalkTests`, `JpegPassThroughSoundnessTests`,
   `DocumentJsonTests`).
6. PDF bytes and abort, PNG bytes (`PdfLimitTests`); the CLI (`RenderLimitCliTests`).
7. Calibration against the limits (opt-in `CalibrationTests`: each hostile class, WI-004's builder, PDF classes on
   pages that each end within the machine, asserting metered drawing time; the longest step of each kind; the
   page-end class recorded, not asserted; `APassedThroughJpegAt225MP_IsNotDecoded`), ordinary documents (`OrdinaryDocumentTests`), real pages, gate; Results here.
8. Documents: README (the limits, the exception, wall-clock time, what stays unbounded until WI-007 (a PDF page's
   end, the caller's stream faults, total memory), a caller's own canvas, the PDF picture placeholders and the typeface limit, Letter's highest dpi), WI-001 interface table,
   WI-004's "Rendering is not budgeted yet".

## Verification of rev 9: how each item is answered (rev 10)

| # | Finding (short) | Blame | Rev 10 |
|---|---|---|---|
| Z1 | The generator's stray-byte length skipped nothing; a 5,000-variant seed may not kill the stuffed-byte mutation | TEST-GAP | "Skips the Adobe 0 or JFIF marker"; each such mutation also has a crafted row; the hand-off records kill counts |
| Z2 | DAC and DNL had no row; widenings called narrowings; base files unnamed | TEST-GAP | Rows for DAC and DNL, and their mutation (rev 11); widenings and narrowings named apart (rev 11); base files named |
| Z3 | The resource-cache check needs a purge and no parallel renders | TEST-GAP | Purged before each render; the class runs alone |
| Z4 | The 225 MP check unnamed, without numbers or a task | TEST-GAP | `APassedThroughJpegAt225MP_IsNotDecoded`: under 1 s and under 256 MB growth, in round 8's eight placements (rev 11); in Task 7 |
| Z5 | A decoded picture with alpha is written with a soft mask too | PLAN-GAP | AC6's one-step term: the mask when the image has transparency (rev 11 drops a false "a layer always does") |
| Z6 | AC4's note read as false | PLAN-GAP | The verification's wording, verbatim |

## Task 0, round 8 (rev 8): APPROVE; how each non-blocking item is folded in

| # | Finding (short) | Blame | Rev 9 |
|---|---|---|---|
| Y1 | X2's mutation could fail no test as worded | TEST-GAP | The stuffed-byte row is sized as round 7's; the generator adds stray bytes with such lengths |
| Y2 | "A second SOF allowed" cannot fail through rendering | TEST-GAP | `JpegWalkTests` rows on the raw bytes (two SOFs, no SOF) |
| Y3 | The generated test named mutations its one-sided check cannot fail | TEST-GAP | Each mutation named against the test that can kill it |
| Y4 | "The whole file appears" cannot see a decode that also embeds the file | TEST-GAP | Skia's resource cache must not grow; a 225 MP run per passed placement in the opt-in suite |
| Y5 | The README list; "a picture SkPDF will decode"; "built as round 6 built them" | PLAN-GAP | The list completed; "counts as decoded"; "rounds 6 and 7, or as each row states" |
| Y6 | A shadow layer is written as an image and a soft mask | PLAN-GAP | AC6's one-step term includes the mask |

## Task 0, round 7 (rev 7): how each finding is answered

| # | Finding (short) | Blame | Rev 8 |
|---|---|---|---|
| X1 | Lossless JPEGs without JFIF or Adobe markers are read as RGB and decoded, yet the walk passed them | AC-GAP | Inverted: only SOF0, SOF1 or SOF2 of precision 8 can pass (AC4); rows for lossless, arithmetic and 12-bit files; the generated soundness test |
| X2 | A stuffed `FF 00` could be read as a marker with a length | AC-GAP | Only listed marker codes are walked; anything else counts as decoded (AC4); a row and a mutation |
| X3 | Rows left open the IDs and sizes that make their mutations visible; round 6's w dropped | TEST-GAP | Each row states its IDs and sizes, built as round 6 built them; w restored |
| X4 | The soundness check's scope (turned and mirrored files) and a partial byte match | TEST-GAP | Keyed on the plan's whole verdict; the file's whole byte sequence must appear |
| X5 | The calibration row for PDF classes | TEST-GAP | PDF classes on pages that each end within the machine; metered drawing time asserted; what the page-end class records (row and Task 7) |
| X6 | Prose | PLAN-GAP | Task 0's line; U7 names item g; memory wording in Measurements; "the YCbCr one"; the content table's preface; `JpegWalkTests` in the interface table and Task 5; "counting from 0"; the stub; rev 6's note and F8's row |

## Task 0, round 6 (rev 6): how each finding is answered

| # | Finding (short) | Blame | Rev 7 |
|---|---|---|---|
| W1 | AC4's words left three details open (`JFIF\0`, lengths with or without the length field, markers without a length) | AC-GAP | The walk stated byte by byte, stricter than libjpeg for fill bytes, TEM and RST; SkPDF itself the test's oracle; a row and a mutation for each detail (AC4) |
| W2 | Rows missing (round 5's i, j, k; W1's files); the after-SOF row did not fix IDs; "unwalkable passed" untestable | TEST-GAP | Every crafted case has a row; the after-SOF row has IDs 1, 2, 3; the stricter walk makes "unwalkable passed" testable (a TEM before the tables, which SkPDF passes, is a placeholder under the plan's rule) |
| W3 | The calibration row asserted a bound for the page-end class | TEST-GAP | Recorded, not asserted |
| W4 | The close's payload understated | PLAN-GAP | Per page, per image a page uses, per graphic state, per image or layer; about 150 MB at the load limit (AC6) |
| W5 | The WI-007 stub dropped findings and misattributed two | PLAN-GAP | The stub completed: V2's second case, the ordinary-document constraint, the silhouette's own size, round 3's per-page alternative |
| W6 | Wording | PLAN-GAP | "Memory" for what round 3 measured; "more memory" for round 5's kinds; the CMYK row; F2, F11 and "Not in scope" name the page's end; U7's row marked |

## Task 0, round 5 (rev 5): how each finding is answered

| # | Finding (short) | Blame | Rev 6 |
|---|---|---|---|
| V1 | Long numbers, a colour per instance, Type3 fonts and conic points make SkPDF hold more than the weights | AC-GAP | Moved to WI-007 with the page-end bound (the owner's decision); the stub carries the data and round 5's recommendations |
| V2 | SkPDF culls by device bounds, not the frame: rotated text held 217 times its count | AC-GAP | Moved to WI-007, as V1 |
| V3 | Silhouettes drawn without a path were not measured | TEST-GAP | Moved to WI-007, as V1 |
| V4 | AC4's wording was not libjpeg's rule; crafted RGB files passed it | AC-GAP | libjpeg's rule marker by marker (AC4); a row for each crafted case and a mutation for each part of the rule |
| V5 | AC5's "culled lines counted" mutation survived | TEST-GAP | The page content fraction left AC5 with the count |
| V6 | The close's payload was understated | PLAN-GAP | About 55 B for each image or layer written (AC6); WI-007 lists page dictionaries held until the close |
| V7 | The gate test should read only `/Contents` | PLAN-GAP | Moved to WI-007 with the test |
| V8 | Prose | PLAN-GAP | The silhouette row names the ellipse it includes; the picture-with-a-line figure removed; the Design's counters include WordArt runs; "held" now has one meaning; the WI-007 stub no longer cites a WI-006 limit |

## Task 0, round 4 (rev 4): how each finding is answered

| # | Finding (short) | Blame | Rev 5 |
|---|---|---|---|
| U1 | The page content count (draw calls, points, glyphs) did not track what SkPDF holds: an ellipse 8 KB for one call; culled text over-counted; held and written undefined | AC-GAP | (Moved to WI-007 in rev 6.) Redesigned from data (Measurements): held bytes; a measured upper bound per draw site, per primitive or per point of a path the renderer builds; culled lines skipped; layer content not counted; where each count is added (AC6, Design) |
| U2 | No test could fail when a kind was under-counted or counted late; AC5's page content had no observer | TEST-GAP | (Moved to WI-007 in rev 6.) `EachDrawSite_CountsAtLeastWhatSkPdfHolds` against the inflated content stream; a draw-sites counter in the refusal test; AC5's test renders the PDFs |
| U3 | JPEGs stored as RGB pass the "1 or 3 components" rule but are decoded | AC-GAP | libjpeg's YCbCr-or-grayscale rule; unparsable headers count as decoded; rows for each (AC4); README |
| U4 | The one-glyph threshold read through `SilhouetteScale` (300/72 in a PDF) | AC-GAP | The canvas's scale computed directly (AC3); a test row |
| U5 | The WordArt-run checkpoint's removal was invisible | TEST-GAP | A WordArt-runs counter and an empty-runs row; AC2's limits apply to WordArt |
| U6 | The commit test's failure point could not see a forward-order undo; temporaries after a failure | TEST-GAP | A move into place fails after another succeeded; the seam's call order asserted; no temporaries remain (AC7) |
| U7 | Prose | PLAN-GAP | (Item g moved to WI-007 in rev 6.) 16,384 resolutions; Task 0's line; sources of rounds 3 and 4; the layer image in the step; `MaxPicturePixels`; the close's other payload; N12's row and the WI-007 stub; the README's leftovers |

## Task 0, round 3 (rev 3): how each finding is answered

| # | Finding (short) | Blame | Rev 4 |
|---|---|---|---|
| S1 | The chunked-text construction admitted variants that change pixels; the named mutant was invisible to the named rows | TEST-GAP | The construction written out (Design); rows red against all four variants, CJK included; "taller" defined by the canvas's scale |
| S2 | The metering test's PDF half failed against a correct meter | TEST-GAP | PDF of empty pages (AC8, test row) |
| S3 | A PDF page's end was neither metered nor bounded | AC-GAP | (Moved to WI-007 in rev 6.) A per-page content count, refused before the page ends (AC6); in the worst case and AC5 |
| S4 | CMYK JPEGs are decoded by SkPDF but counted as passed through | AC-GAP | Passed through only with 1 or 3 components and origin TopLeft; CMYK and mirrored rows; README list (AC4) |
| S5 | A caller's canvas left rotated or in a layer after a stop | AC-GAP | Restored to its entry save count (AC3); test row |
| S6 | The commit's success path unspecified | AC-GAP | Backups deleted on success; restore in reverse order; leftover backups untouched (AC7); two tests |
| S7 | A failed restore could not be tested deterministically | TEST-GAP | `ICliFiles` seam (AC8, Design) |
| S8 | The counting stream's `Position` | PLAN-GAP | `Position` is its count (AC6), which also ends today's crash on streams that cannot seek; test row |
| S9 | The typeface pre-pass resolved every name, unmetered | AC-GAP | Stops at `MaxFontNames` + 1 before resolving (AC1); test row; in the worst case |
| S10 | Empty paragraphs and WordArt runs fell between checkpoints | AC-GAP | Checkpoints before each paragraph and WordArt run (AC3); test rows |
| S11 | AC9 contradicted the new `MaxPixels` | PLAN-GAP | The exception named in AC9; README states Letter's highest dpi |
| S12 | Observers and rows | TEST-GAP | WordArt build counter; the picture checkpoint dropped as equivalent; a WordArt layer row; rounded out; refusal defined and sticky; "counted refusal" defined |
| S13 | Prose | PLAN-GAP | Ranges for the three rows; 37 minutes; Task 7 cited; Task 1 lists the largest picture; the allocation sentence; load and pre-pass in the worst case; the runs default's reason; WordArt fonts not counted; the README items |

## Task 0, round 2 (rev 2): how each finding is answered

| # | Finding (short) | Blame | Rev 3 |
|---|---|---|---|
| R1 | The encoding test could not fail; observers missing; the throwing path untested | TEST-GAP | A clock that jumps on stream writes; diagnostics counters named; `ARenderThatThrows_StillAddsItsTime` |
| R2 | CLI tests could not inject limits or a clock | TEST-GAP | The CLI's internal `Run` with limits, clock and load limit (AC8) |
| R3 | One text line was one uninterruptible step | AC-GAP | Runs of at most 64 glyphs (1 for fonts over 256 px), each after a checkpoint, pixel-identical (AC3, AC9) |
| R4 | A stop still closed the PDF | AC-GAP | `Abort()` on any exception; a stopped context writes nothing (AC7) |
| R5 | The layer guard needed glyph paths and the renderer's rule | TEST-GAP | Checked at draw time in `DrawShadow`, from the canvas's matrix; rotation, offset, clip and non-finite rows (AC4) |
| R6 | The CLI's and the exporters' counts undefined | TEST-GAP | Defined (Design); `TheCheck_MatchesWhatTheExportersAdd` |
| R7 | Defaults against ordinary inputs: linked stories, passed-through JPEGs, font bytes | AC-GAP | 8,388,608 characters (the import's own total) and a run limit; the PDF picture cap only for pictures SkPDF decodes; typefaces counted (36) |
| R8 | 300 s too close to the ordinary maximum; wall clock not stated | PLAN-GAP | At least 600 s and ten times the slowest ordinary drawing (Task 1 first); README states wall-clock semantics |
| R9 | The worst case was wrong: PDF bytes, the hang, memory, page end and close | AC-GAP | PDF byte limit back (AC6); the hang named; memory stated as single allocations only; page end and close in the worst case |
| R10 | The commit was not safe on the next run | AC-GAP | Unique backup names; failed restores kept and named; duplicate outputs refused; the second of three targets locked in the test |
| R11 | Per-element limits were not in `PageRenderer.Render` | AC-GAP | AC2, tested through a caller's canvas |
| R12 | `FontResolver` past its cap | AC-GAP | Resolves uncached; the key is (name, bold, italic) (AC4) |
| R13 | Task 1 needed later tasks | PLAN-GAP | Task 1 measures ordinary documents only; calibration against the limits is Task 7 |
| R14 | Prose nits | PLAN-GAP | Fixed: `WordArt.Paint` is given the context; the 430 s includes drawing; 17 h is at 150 dpi; ranges; the builder in Task 7; `Page` when known; the PNG-bytes row; the WI-007 stub |
| R15 | `Exporter.MaxPixels` admitted pages that cannot be allocated | AC-GAP | 500,000,000 (AC4) |

## Task 0, round 1 (rev 1): how each finding is answered

| # | Finding (short) | Blame | Answer (rev 2, with rev 3's changes) |
|---|---|---|---|
| F1 | Lines and rotated layers draw far more than the frame | AC-GAP | No area model: metered; the PDF layer guard uses the layer's own bounds under rotation (AC4) |
| F2 | WordArt drawn glyph by glyph | AC-GAP | A checkpoint per glyph (AC3); the glyph count (AC2) |
| F3 | Decodes repeated by cache eviction | AC-GAP | Metered (a checkpoint before each decode) |
| F4 | Crops decode the whole source in a PDF | TEST-GAP | Metered; a calibration class (Task 7); the PDF byte limit (AC6) |
| F5 | Shadowed content drawn twice | AC-GAP | Metered (a checkpoint between shadow and element) |
| F6 | Coefficients depend on source size, lines, typefaces | TEST-GAP | No coefficients; typefaces counted (AC1) |
| F7 | No backstop for unlisted classes | AC-GAP | The meter (AC3), with text lines chunked (rev 3) |
| F8 | Defaults arbitrary | PLAN-GAP | Each default stated against its ordinary maximum (AC5), measured first (Task 1) |
| F9 | Margin and machine state | PLAN-GAP | No margin to size; the reference state for the opt-in suite (Task 1) |
| F10 | Fonts written at `Close`; the PDF limit's bound | TEST-GAP | The bound includes the close's payload, bounded by `MaxPdfTypefaces` (AC6) |
| F11 | A caller-stream fault during a picture write hangs | TEST-GAP | Named in AC6; contained in WI-007 |
| F12 | Picture sizes for the estimate; the read-retry test | PLAN-GAP | No estimate; the PDF cap is decided at draw time; the cache's verdict unchanged |
| F13 | The ledger keyed by page equality | PLAN-GAP | No ledger; counting defined (Design) |
| F14 | Public API for the CLI | HOUSE-RULE | `RenderLimits.Check`, `RenderOutputs` and its members, the CLI's internal `Run` (interface coverage) |
| F15 | `PageRenderer.Render` bypassed every limit | AC-GAP | The meter and the per-element limits are in it (AC2, AC3) |
| F16 | Shadowed JPEGs are decoded by SkPDF too | AC-GAP | The PDF cap covers every picture SkPDF decodes, shadowed JPEGs included (AC4) |
| F17 | Memory classes missing | TEST-GAP | Crash guards (AC4); the total in WI-007 |
| F18 | AC7's CLI message untested | TEST-GAP | Load message of its own; CLI test through the internal `Run` |
| F19 | A shared `FontResolver`; long names | TEST-GAP | The cache's own limit; names cut at 256 (AC2, AC4) |
| F20 | Warnings cap and de-duplication set | AC-GAP | WI-007 |
| F21 | The linear-time test's counter | TEST-GAP | Dropped with the estimate |
| F22 | A wall-clock watchdog | PLAN-GAP | WI-007 |
| F23 | A failed move left mixed outputs | AC-GAP | Backup and restore, safe on the next run (AC7) |
| F24 | Table rows and the 400-typeface figure | PLAN-GAP | Measurements with every source and range |
| F25 | No stated worst case | AC-GAP | AC6 |
| F26 | The requirements table claimed answers it did not give | PLAN-GAP | Rewritten below |
| F27 | Seven small items | PLAN-GAP | (a, b) dropped with the estimate; (c) both orders tested; (d) injected caps; (e) CLI messages asserted; (f) Task 1 needs no new API; (g) no CLI option, said in Design |

## Requirements carried from the WI-005 reviews

| Requirement | Where |
|---|---|
| F1 Injectable limits, boundary tests, pinned defaults, deterministic observers | AC8, AC1 |
| F2 A numeric worst case per class (time, peak memory), WI-004's hostile builder included | AC6 and Task 7 (time and output, but a PDF page's end); memory: single allocations here; a page's end and the total in WI-007 |
| F3 Pages and PNG output (pixels and bytes) per command | AC1, AC6 |
| F4 Drawn area per command; PDF shadow layers by area | Changed: drawing time (AC3); PDF layers by size as a crash guard (AC4) |
| F5 WordArt glyphs before paths are built; outline | AC2 (glyph count before building); AC3 (per-glyph checkpoint) |
| F6 Runs, paragraphs, glyph area, layout below the frame, warnings, long names | Changed: runs limited (AC2) and metered (AC3); long names cut (AC2); warnings in WI-007; layout below the frame is not stopped: it is metered |
| F7 Picture decoding that cannot refuse ordinary documents | AC3 (decodes metered), AC5 (the catalogue) |
| F8 PDF embeds; SkPDF's full-size decode bounded | AC4 (the whitelist, with its generated soundness test), AC6 |
| F9 WI-004's ordinary large publications | AC5 |
| F10 CLI stages every output, commits only on success | AC7 |
| F11 PDF size through a counting stream; `Abort()`; watchdog | Size and `Abort()`: AC6, AC7 (a page's end can pass the limit by what it writes: WI-007); fault containment and the watchdog: WI-007 |
| F13 Device-space extents, finite | Changed: no extents estimated; the layer guard maps the layer's bounds through the canvas's matrix, clipped, non-finite refused (AC4) |
| F16, F18 Interface coverage, task → test map, documents | Test plan, Tasks |
| N12 Line spacing ≤ 0 | Metered (Design) |
| N12 `FontResolver` cache bounded | AC4 |
| N12 Per-page memory of PDF vector content | WI-007 (with the page's end; AC6 states the gap) |
| N12 The stream after a refusal | AC1 (nothing written), AC7 (aborted after a stop) |
| R10 WordArt by glyph bounds; envelope `Adjust` | Changed: metered per glyph; `Adjust` is not range-checked |
| R10 F17 Counters sticky | AC1, AC3 |
| R10 Memory of WordArt paths, encoded pictures, decoded copies | WI-007 (AC2's glyph count bounds one WordArt's paths) |

## Not in scope

Making any class faster; rendering fidelity; a GUI; a PDF page's end, fault containment for the caller's stream,
the warnings cap and the full memory bound (WI-007, `WI-007-pdf-output-and-memory.md`).
