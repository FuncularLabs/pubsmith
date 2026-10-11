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
- **Rev 12:** Task 1's measurements (Measurements, "Task 1"): every default stands, `MaxDrawTime` at 600 s.
- **Rev 13:** Tasks 2 to 8 done; Results below, with what implementing them changed.
- **Rev 14:** the implementation's independent review (round 1: one Medium, ten Low, four Nit) answered: three more
  checkpoints (AC3), a PDF's close stated as unbounded by a count until WI-007 (AC6), a new output that cannot be
  removed named (AC7), `Check`'s refusals not sticky (AC1), and the tests the findings asked for (table at the end).
  Results updated.
- **Rev 15:** the verification of rev 14's fixes (round 2: four Low, three Nit; R2 and R12 partly fixed) answered: a
  checkpoint every 1,024 words as a paragraph's lines are gathered, and a line's metrics read once per run (AC3); AC6's
  steps stated by kind; AC9's two differing comparisons explained by the variation between processes the verification
  found at `a914605` too; the remaining pins (table at the end).
- **Rev 16:** the verification of rev 15 (round 3: two Low, two Nit) answered. The text and WordArt step sizes are
  measured by runs drawn to the end under the default limits (rev 15's numbers did not measure those steps: rev 16
  said they came from runs stopped before them, which rev 17 corrects), and AC6 names the two kinds that grow with an element limit; a line's metrics pinned against the font's own;
  AC9's rule compares against a set of warm renders; the warning's wording, now pinned (table at the end).
- **Rev 17:** the verification of rev 16 (round 4: three Low, two Nit) answered, the third round in a row to find the
  text step inventory wrong, so this time the code changed: a word longer than 1,024 UTF-16 units is measured and
  positioned a chunk at a time, each distinct glyph measured once, bit for bit the font's whole-word results; a WordArt
  run is looked up and measured in chunks; a checkpoint before each chunk and before each WordArt glyph laid out
  (AC3). Measuring one word of 60,000 distinct glyphs at 250 pt took one step of 23.5 s; it now takes steps of at most
  260 ms. Also: the reverse line-metrics row; both calibration switches exact, each text row checked for its own
  shape; rev 16's account of rev 15's numbers corrected (two rows had drawn other rows' shapes); the wording; a test
  name (table at the end).
- **Rev 18:** the verification of rev 17 (round 5: three Low, three Nit) answered. Rev 17's chunked measuring held
  bit for bit for valid text (the review found no difference in 19,110 rows over 273 installed families) and its steps
  held their bounds; its residuals are at the edges. Text that is not valid UTF-16 now gives what the whole-piece calls
  give (no glyphs, width 0) rather than measuring and drawing the valid chunks; the table of advances is one per
  context, reused, as the per-piece tables made many long words three to six times slower; a checkpoint before a piece
  with no glyphs is passed over; `Chunks` ends by construction and its guard at the text's end is pinned; each
  step-theory row is checked against what its name says it builds; the mutation bookkeeping corrected (table at the
  end).
- **Rev 19:** the verification of rev 18 (round 6: one Low, two Nit; every earlier finding resolved; the invalid-text
  rule proved from Skia's own UTF-16 count and fuzzed over 80,600 rows without a difference) answered. What the shared
  table forgets is now observable and pinned (a table that forgot nothing passed every test while its cost grew with the
  square of the pieces); text that is not valid UTF-16 gets the known result, width 0 and no glyphs, without the
  whole-piece call that scanned the piece in one step; the first commit's message, which claimed more than the code in
  seven places, is reworded, and the implementation re-committed as one commit (table at the end).
- **Rev 20:** the verification of rev 19 (round 7: four Nit; every earlier finding resolved; the invalid-text constants
  exact over 40,040 texts, bit for bit over 33,360 comparisons in four styles) answered. The clause "without those
  calls" moves from AC3 to Design, where it is held by reading and recorded by the step theory (no ordinary test can see
  it); the table's failure path is pinned (a round 6 mutant called unobservable is killed); the inventory names three
  more steps that copy or count a whole piece or element, under 20 ms each; the commit message's WordArt
  sentence and two doc comments say exactly what the code does (table at the end).
- **Rev 21:** the verification of rev 20 (round 8: one Low, three Nit, one of them a residual) answered. A text element
  of millions of paragraphs without runs made the element's check one step of seconds, since no limit counted such a
  paragraph: it now counts as one run, and the runs are refused as they are counted, so every walk in the check, and a
  WordArt's walk to its first run, is at most the limit plus one paragraph long (AC2). The counted pre-check's walk over
  the document is stated in AC6's worst case. The commit message says "piece" where the code chunks per piece; the last
  unscoped doc comment is scoped; the numbers quoted for the same steps agree (table at the end).
- **Rev 22:** the verification of rev 21 (round 9: one Medium, four Nit, and one residual; rev 21's fixes held) answered.
  The CLI planned every PNG page's output, and checked its folder, before the counted check refused the page count: a
  document of a million tiny pages took 566.6 s to be refused when the output folder was given in 8.3 short form. The
  check now comes first, and the PNG path is expanded once (AC7). Also: the in-loop refusal pinned for paragraphs that
  have runs; the runs limit's summary and the README say a paragraph without runs counts as one; the AC2 test-plan row
  describes its own tests again; the Results' "34 ms against 5 ms" corrected; the commit message says only the runs
  are refused as they are counted (table at the end).
- **Rev 23:** the verification of rev 22 (round 10: one Low and three Nit) answered. AC7's new clause had no test that
  can fail: its test saw only which error wins, so naming every output before the check, and comparing their paths
  after it, passed. Two tests now give an output a path holding a NUL character, which fails as it is named: over the
  page limit the command is refused by the limit, and within it by the path. AC9 names the one change of output rev 22
  made: PNG pages are named from the output path once made full, so on Windows `sheet.png.` gives `sheet-p1.png`, where
  it gave `sheet.png-p1`. AC6's worst case adds a `.pub`'s import before the check and the planning's time, and gives
  round 9's PDF timings as measured; rev 22's note is made exact (table at the end).
- **Rev 24:** the verification of rev 23 (round 11: CLEAN, with one Nit) answered. AC6's worst case said a `.pub` is
  imported before loading; its import replaces loading, and its write includes the import's report, which only its
  issue count bounds (table at the end).

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

**Task 1 (2026-10-02): what AC5 and the defaults rest on.** A Release build of `a914605`, in a throwaway copy with a
counter around `PageRenderer.Render`'s body (what the meter will measure). One context per command, as the CLI runs
one: the PDF, then each page's PNG at 300 dpi. Drawing time is the best of 5 sequential runs (the PNG pages drawn
through `RenderBitmap`, without encoding); bytes come from a separate run. Machine: the development machine above,
power plan High performance. It was not quiet: three idle Vite dev servers used about 5 of the 24
logical cores throughout, and 7 GB of memory was free. The slowest command draws in a quarter of the 60 s AC5
allows, so even a 4× slowdown from that load could not change a default.

| Command (PDF + PNG at 300 dpi) | Pages | PNG pixels | PDF typefaces | Font names | Largest text box: characters, runs | PDF bytes | PNG bytes | Drawing: PDF, PNG, command |
|---|---|---|---|---|---|---|---|---|
| WI-004's 300 pages of one linked story, as its test builds it | 600 | 2.5 GP | 1 | 1 | 450,000; 3,000 | 0.6 MiB | 11.7 MiB | 0.02, 0.03, 0.04 s |
| WI-004's 1,000 pages with a master | 2,000 | 8.4 GP | 1 | 1 | 3,000; 20 | 1.1 MiB | 47.0 MiB | 0.16, 0.77, 0.93 s |
| WI-004's 100 pages of 200 shapes | 200 | 0.84 GP | 0 | 0 | none | under 0.1 MiB | 3.9 MiB | 0.02, 1.25, 1.28 s |
| Round 1's book: 1,000 pages of 6,000 visible characters | 2,000 | 8.4 GP | 2 | 1 | 6,000; 40 | 6.1 MiB | 742.7 MiB | 2.22, 6.33, 8.55 s |
| The catalogue: 100 pages, each a distinct 14.4 MP JPEG of 2.0 MiB | 200 | 0.84 GP | 1 | 1 | 860; 1 | 167.7 MiB | 505.1 MiB | 0.25, 14.25, 14.51 s |
| The 192 real files: the largest of each, per file | 22 | 0.093 GP | 8 | 8 | 2,459; 42 | 11.4 MiB | 9.3 MiB | 0.53, 0.33, 0.77 s |

In the real files, the largest picture file is 3.37 MiB, the largest picture 14.4 MP, the largest shadow layer in a
PDF 0.15 MP, the largest page 16.8 MP at 300 dpi and the longest WordArt 26 characters. Their `doc.json` files are
at most 0.71 MiB; the book's is the largest of all, at 25.2 MiB. The longest single element drew in 309 ms. Across the
real files SkPDF never wrote more Type0 fonts than the counted typefaces. In five files it wrote fewer (counted
typefaces it never embedded, the safe side). Two of those five also hold two Type3 font objects each; whether each
pair comes from one typeface was not checked.

Against the defaults (AC5: every counted value at most half its limit, typefaces at most a quarter, drawing at most a
tenth of `MaxDrawTime`):
- `MaxDrawTime`: the slowest command (the catalogue) draws in 14.5 s. Ten times that, rounded up to a minute, is
  180 s, under the 600 s floor, so the default is 600 s; the slowest command uses 2.4 % of it.
- The largest ordinary value against each counted default:
  - pages 2,000 of 20,000 (10 %);
  - PNG pixels 8.4 of 20 GP (42 %);
  - PDF typefaces 8 of 36 (22 %);
  - font names 8 of 4,096;
  - characters 450,000 of 8,388,608 (5.4 %); runs 3,000 of 262,144 (1.1 %); WordArt glyphs at most 26 of 16,384;
  - PDF bytes 167.7 MiB of 2 GiB (8.2 %); PNG bytes 742.7 MiB of 16 GiB (4.5 %);
  - a picture file of 3.37 MiB of 256 MiB (1.3 %); a picture of 14.4 of 64 MP (22.5 %); a PDF shadow layer of 0.15
    of 64 MP;
  - `doc.json` 25.2 of 256 MiB (9.8 %); a page of 16.8 MP of `MaxPixels`' 500 MP.

  All within AC5, so every default stands as planned.

## Acceptance criteria

| AC | Criterion |
|---|---|
| AC1 | **Counted limits, before anything is written.** Known exactly from the document and the outputs asked for, and checked before drawing: pages written, PDF and PNG together (`MaxPages`, default 20,000 per context); PNG output pixels (`MaxPngPixels`, 20 gigapixels per context); distinct resolved typefaces of one PDF's text boxes (`MaxPdfTypefaces`, 36; WordArt is drawn as paths, so its fonts are not embedded and not counted). A refusal throws `RenderLimitException` (`Limit`, `Value`, `Page` when known, `Element` when known) before anything is written: by `Exporter` for its own call, and by the CLI for the whole command through the public, pure `RenderLimits.Check(document, outputs, context)`, which computes exactly what the exporters will add (Design). Before it resolves typefaces, the check cuts names to 256 characters, de-duplicates them and stops at `MaxFontNames` + 1 distinct names (AC2's refusal), so it resolves at most 4,096 names (16,384 resolutions: each name in up to four styles). A context's counters only grow, and after any `RenderLimitException` from a render every later render with it throws before writing (a refusal by `Check`, which is pure, does not mark the context). |
| AC2 | **Per-element limits, wherever the page is drawn.** `PageRenderer.Render` (the exporters, and a caller's own canvas) refuses, before drawing the element, a text box of more than `MaxTextBoxCharacters` (8,388,608, the most text an import places in all) or more than `MaxTextBoxRuns` (262,144: above the 250,000 units that bound the runs an import places), and a WordArt of more than `MaxWordArtGlyphs` (16,384) glyphs; and refuses a document naming more than `MaxFontNames` (4,096) distinct font names in one context, counted where fonts are resolved. These text limits apply to every text element, WordArt included. A paragraph without runs counts as one run (it is laid out as an empty run and resolves a font as one), and the runs are refused as the paragraphs are counted, so the element's check walks at most `MaxTextBoxRuns` + 1 paragraphs before it refuses (rev 21: an element of millions of empty paragraphs, which no other count saw, took 3–6 s in one step). Font names longer than 256 characters are cut to 256 before they are resolved, as the importer cuts them. |
| AC3 | **A drawing-time limit.** Every render through `PageRenderer.Render` adds the time spent drawing to its context, from the context's `TimeProvider` (the system's by default), in a `finally` (a render that throws still adds its time); when the total passes `MaxDrawTime` (default 600 s, and at least ten times the slowest ordinary drawing time Task 1 measures) the render stops at the next checkpoint with `RenderLimitException`. Checkpoints: before each element; between a shadow and its element; before each paragraph and each run is laid out; before each text line is drawn and before each run of at most 64 glyphs of a piece (1 glyph when the font is more than 256 device pixels tall: its size times the square root of the absolute determinant of the canvas's total matrix, computed directly; `SilhouetteScale` is not used, since it answers 300/72 for a PDF) is drawn, by the construction in Design, which is pixel-identical to drawing the piece at once; before each WordArt run and glyph is built, before each WordArt glyph is placed (warped) and before each WordArt glyph is painted; and, added in rev 14, every 1,024 characters of a run as it is laid out and before each line is laid out; and, added in rev 15, every 1,024 words of a paragraph as its lines are gathered; and, added in rev 17, before each chunk after the first of a piece longer than 1,024 UTF-16 units (a chunk is at most 1,024 units and never splits a surrogate pair) as it is measured and again as its glyphs are positioned, each chunk's glyphs looked up, the ones the piece has not met measured, then added or placed (bit for bit the font's whole-piece width and positions; rev 18: text that is not valid UTF-16 gives what the whole-piece calls give, no glyphs and width 0; how, in Design), before each chunk after the first of a WordArt run as it is looked up and measured, and before each WordArt glyph is laid out in an envelope; and, added in rev 18, before a piece with no glyphs is passed over. (A picture needs no checkpoint of its own: its decode and draw follow the element's or the shadow's checkpoint directly.) On a caller's own canvas, a stop restores the canvas to the save count it had on entry (no layer left open, no rotation or offset left); the exporters' canvases are discarded or aborted instead. PNG encoding, a PDF page's end and a PDF's close are not drawing time (AC1 and AC6 bound encoding and the close; a page's end is WI-007's). `MaxDrawTime` is wall-clock time on the machine that renders: it includes waits for disk and memory, so a refusal near the limit is not reproducible (the README says so); ordinary documents sit far below it (AC5). |
| AC4 | **Crash guards.** In a PDF made by `ToPdf`: a picture that counts as decoded, that is anything but a JPEG in the one passed-through shape described below, with EXIF origin TopLeft (a mirror counts as turned), uncropped and drawn outside a shadow layer, of more than `MaxPdfPicturePixels` (64 MP), is the placeholder with `image-too-large`, a verdict for that PDF only (the same picture draws in a bitmap, in either order with one context). A JPEG is passed through only in one shape, and every other picture counts as decoded. Its markers, read from the bytes the cache has already read: after the SOI, each marker is exactly one 0xFF byte followed by one of these codes, each with a two-byte big-endian length that counts itself and stays inside the file, up to the first SOS: one SOF0, SOF1 or SOF2 (baseline, extended or progressive Huffman coding) of precision 8 and 1 or 3 components; DHT; DQT; DRI; APP0 to APP15; COM. Anything else where a marker is expected (another SOF, such as lossless or arithmetic coding; a second SOF; a stuffed 0x00; a fill byte; a TEM or RST; any other code) makes the picture count as decoded. A JFIF marker is an APP0 whose data after the length is at least 14 bytes and starts with the five bytes `J F I F 0x00`; an Adobe marker is an APP14 whose data after the length is at least 12 bytes and starts with the five bytes `A d o b e`, its transform being data byte 11, counting from 0. libjpeg's colour rule then decides: 1 component is grayscale; with 3, a JFIF marker makes it YCbCr; otherwise the last Adobe marker makes it RGB for transform 0 and YCbCr for any other; otherwise component IDs R, G, B (0x52, 0x47, 0x42) make it RGB and any others YCbCr. Only a grayscale or YCbCr JPEG is passed through. (Every picture SkPDF decodes counts as decoded here; SkPDF also passes through some files the shape excludes, which therefore cost a placeholder only when over 64 MP.) In a PDF, too, a layered shadow whose layer, at the PDF's 1 pixel per point, under the element's rotation and the shadow's offset, clipped to the page and rounded out as SkPDF allocates it, would exceed `MaxPdfLayerPixels` (64 MP), or is not finite, is refused before the layer is made: `RenderLimitException`, sticky like every other. In every output: a picture file larger than `MaxPictureFileBytes` (256 MiB; an import can write a larger one, up to its picture budget, from an uncompressed picture; Task 1 records the largest real picture file) is not read: placeholder and `image-too-large`. `FontResolver` caches at most 4,096 (name, bold, italic) keys, its own limit (so sharing it between contexts cannot grow it); past it, a font resolves as before, uncached. `Exporter.MaxPixels` becomes 500,000,000 (one page's bitmap must stay under SkiaSharp's 2 GiB allocation: a Letter page at 2,400 dpi, 538.6 MP, failed to allocate); `DocumentJson.Load` refuses a file of more than `MaxLoadBytes` (256 MiB) by reading through a limited stream, with its own message naming the file; the CLI prints it and exits 1. |
| AC5 | **Ordinary documents never meet a limit.** The 192 real files, WI-004's three ordinary large publications (a 1,000-page book with a master; 300 pages of one linked story; 100 pages of 200 shapes) and a 100-page catalogue of distinct 14.4 MP photos, in PDF and in PNG at 300 dpi: every counted value at most half its limit (typefaces at most a quarter), and every drawing time at most a tenth of `MaxDrawTime` on the reference machine (Task 1 measures them before the defaults are fixed). |
| AC6 | **Output is bounded, and the worst case is stated, gap included.** PDF bytes: `ToPdf` writes through a counting stream (its `Position` is its own count, so a stream that cannot seek works; today it crashes the process) and checks the count at each checkpoint, after each page's end and before its close; past `MaxPdfBytes` (2 GiB) it aborts the document and throws; the bound is the limit plus one step's writes (one image: a decoded picture or a layer's image of at most 64 MP, with its gray soft mask when it has transparency, or a passed-through JPEG of at most `MaxPictureFileBytes`), plus one page's end, plus the close's payload (at most `MaxPdfTypefaces` fonts, measured up to 13.3 MB each; the close's time is not bounded by a count until WI-007, rev 14: it grows faster than the distinct glyphs drawn from each typeface, which nothing counts, so it is bounded only through `MaxDrawTime`, and the document's dictionaries and cross-reference table: about 230 B a page, 48 B a page for each image the page uses, 34–36 B for each distinct graphic state such as a line width, and about 55 B for each image or layer written; about 150 MB for a document at the load limit, extrapolated). **A PDF page's end is not bounded until WI-007:** SkPDF holds a page's whole content until the page ends, then writes it at once; Task 0 measured, per second of drawing, up to about 230 MB of memory, 57 MB written and an end of 4.6 times the drawing time (WordArt), and more memory for kinds round 5 found, so one hostile PDF page can still exhaust memory before `MaxDrawTime` stops it; the README says so. PNG bytes: `ToPng` counts each encoded page against `MaxPngBytes` (16 GiB per context) before writing it. A command's worst case, recorded in Results from Task 7's measurements: loading (`MaxLoadBytes`), the counted check (at most 16,384 font resolutions), drawing time `MaxDrawTime` plus the longest step of each kind (the Results list the kinds: drawing onto the page grows with the page's pixels up to `MaxPixels`, a picture's decode with the picture's pixels, and text and WordArt are in steps of at most 1,024 characters scanned (measuring at most 2,048 UTF-16 units' glyphs), 1,024 UTF-16 units of a long piece or a WordArt run looked up and measured, 1,024 words gathered, 64 glyphs drawn or one WordArt glyph, a step's time growing with the distinct glyphs it meets and their size; two kinds grow with an element limit: a line's pieces scanned for its metrics and its words summed before it is drawn, up to 4,194,304 words, and a WordArt's passes over all its outlines, up to 16,384 glyphs; and three steps copy or count a whole piece or element, under 20 ms at the limits in Release: a long piece copied out of its run, its glyph and position lists allocated and copied when it is positioned, and a WordArt's characters counted before it is refused; and the element's check itself, which walks at most the runs limit plus one paragraph for runs, characters, WordArt glyphs and font names, 29–37 ms at the runs limit; rev 17, rev 20, rev 21), each PDF page's end (unbounded, as above) and the close, PNG encoding at `MaxPngPixels` at the measured rate, and the bytes above. Memory: each single allocation the renderer makes is bounded by AC4, `MaxPicturePixels` and `MaxPixels`; what SkPDF holds for a page, and the total, are not bounded until WI-007. A fault in the caller's stream while SkiaSharp writes a picture can still hang the process (the WI-005 F11 hang); WI-007 contains it, and the README says so until then. |
| AC7 | **A stop leaves nothing finished.** On any `RenderLimitException` (or any exception) inside `ToPdf`, the document is aborted, never closed: the stream holds no trailer and no fonts written after the stop. The CLI checks the whole command (AC1) before writing, and before it plans its outputs, whose number grows with the pages (rev 22); refuses two outputs with the same path (compared as full paths, ignoring case on Windows) as a usage error; writes every output to a temporary file; then commits: each existing output is moved to a backup with a unique name, each temporary moved into place; if any move fails, the moves are undone in reverse order (what was moved in is deleted, every output put back from its backup); a backup whose restore fails is kept and named in the error, and so is a new output that cannot be removed (rev 14); when every move succeeded, the command deletes its own backups (one it cannot delete is named in a warning). A failed command deletes its temporaries. Backups and temporaries left by an earlier run that was killed are never touched (the README names them: `<output>.<guid>.backup`, `<output>.<guid>.tmp`). A refused, stopped or failed command leaves every earlier output byte for byte as it was, except what its error names. |
| AC8 | **Injectable, pinned, deterministic.** `RenderLimits` is a public record carried by `RenderContext`, with public defaults pinned by one test; the CLI has an internal entry taking limits, a clock, the load limit and its file operations (move, delete), for its tests. Tests use small injected limits and fake clocks: one that advances a fixed amount at each reading, and one that jumps when the output stream is written, so whether encoding and closing are metered can fail a test (for PDF with empty pages, since SkPDF writes while it draws anything). Wall-clock measurement happens only in the opt-in calibration suite (`PUBSMITH_CALIBRATE`). |
| AC9 | **Nothing else changes.** Checkpoints, checks and chunked drawing change no pixel: every page of the 192 real files renders exactly as on `main` (score 0.000); a long word drawn in runs is identical to the same word drawn at once; the real-file gate stays at 19/106 pages ≤ 3.0, median 8.63, text 429/432; existing tests pass unchanged, except `LimitsTests.LetterAtMaxDpi_IsWithinTheCap` (a Letter page at 2,400 dpi is now over `MaxPixels`, as it could not be allocated; Letter renders up to about 2,310 dpi) and the `MaxPixels` comment. One kind of file name changes (rev 23): PNG pages are named from the output path once made full (`Path.GetFullPath`), not from the path as given, so where making it full changes its last name, the pages follow the changed name, as a single page's file already did. Tried on Windows, that is a trailing dot (`sheet.png.` gave `sheet.png-p1` and now gives `sheet-p1.png`), a trailing space after a name with no extension, an existing file's 8.3 short name, or a path ending in a `.` or `..` segment; every other path tried names its pages as before. |

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
- **Chunked measuring** (rev 17): a piece of at most 1,024 UTF-16 units is measured with `SKFont.MeasureText` and
  positioned with `GetGlyphs` and `GetGlyphPositions`, whole, as before. A longer piece is worked in chunks of at most
  1,024 units that never split a surrogate pair (a chunk that would end in a high half gives it to the next chunk; a
  chunk of one unit is never shortened, so no chunk is empty and the loop ends, rev 18), with a checkpoint before each
  chunk after the first. Each chunk's
  glyphs are looked up (the same glyphs as the whole piece's), the ones the piece has not met are measured with
  `GetGlyphWidths` into a table of 65,536 advances, and the chunk's advances are added (width) or each glyph placed at
  the running sum (positions). Each distinct glyph is measured once per piece: measuring each chunk's glyphs would meet
  a glyph again in every chunk once Skia's cache lets it go, which took a 60,000-glyph word past 600 s. Bit for bit:
  Skia sums a strike's advances in order and scales the sum once; at or below 256 units the scale is 1, and above it the
  width is measured on the canonical 64-unit strike (`SkFont::setupForAsPaths`: no hinting, no embedded bitmaps) and
  scaled once, while positions add each advance already scaled, from the pen at (0, 0), on a horizontal baseline. A
  WordArt run's glyphs and widths are looked up and measured in the same chunks (a glyph's width does not depend on
  its chunk). Rev 18: the table is one per render context (`RenderContext.Advances`, 320 KB), reused from piece to
  piece; each piece begins by forgetting only the glyphs the last one met (`GlyphAdvances.Remembered` says how many, so
  a test can see it: a table forgetting nothing is right but costs the square of the pieces, rev 19), and is measured
  with its own font (the 64-unit strike for a width above 256 units, the font itself for positions). Skia takes one unpaired surrogate to make
  a whole text invalid (no glyphs, width 0); a chunk holding it gives no glyphs although it is not empty, and since
  chunks never split a pair, the text is valid exactly when every chunk is: at the first such chunk the piece, or the
  WordArt run, gets what the whole-piece call gives for such text, width 0 and no glyphs, without the call (rev 19: the
  call scanned the whole piece in one step, up to 8,388,608 units). That the call is not made is held by reading and
  recorded by the step theory's row for such a word (a 5–7 ms longest step against 34–37 ms with the call); no ordinary
  test can see it, so the mutants that restore the call survive by design (rev 20). The chunks before the invalid one
  are measured and discarded, with the lists sized for the whole piece; nothing is drawn.
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
| AC2 | `ElementLimitTests.OverEachElementLimit_IsRefusedBeforeTheElement` (theory: characters, runs, WordArt glyphs, font names; rev 21: paragraphs without runs, in a text box and a WordArt, and mixed with runs; through `ToPng`, `ToPdf` and `PageRenderer.Render` on a caller's canvas; the glyph-build and runs-laid-out counters stay 0 for the refused element) + `ManyEmptyParagraphs_AreRefusedAsTheyAreCounted` (rev 21) and `ManyOneRunParagraphs_AreRefusedAsTheyAreCounted` (rev 22): 1,000 paragraphs, empty or of one run, text box and WordArt, over a limit of 10 are refused at the eleventh, `ParagraphsChecked` 11, nothing built + opt-in step rows for a text box of 262,144 empty paragraphs and a WordArt of 262,143 and one run (rev 21; drawn through `Exporter.RenderBitmap`; `ParagraphsChecked` and `ParagraphsLaidOut` 262,144, or `ParagraphsChecked` 262,144 and one WordArt run built) + `ALongFontName_IsCutTo256` | red. Mutations: limits checked only in `Exporter`; checked after layout; names not cut; rev 21: a paragraph without runs not counted, every paragraph counted one run more, the runs refused after the walk, the paragraphs not counted (`R8-*`), and the round 9 review's: the rule for WordArt only or for text boxes only (`V9-M3`, `V9-M4`); rev 22: the in-loop refusal only for paragraphs without runs (`V9-M1`). Survives by design: the refusing paragraph's own runs walked before the refusal (`V9-M2`: one paragraph more, inside the stated bound). |
| AC3 | `MeterTests.EachKindOfStep_IsStoppedInsideItsElement` (theory: many elements; a text box of many paragraphs, empty ones included; of many runs; many lines; one long line of stacked marks; one WordArt of many runs, empty ones included, and of many glyphs; rev 17: one long word measured and positioned in chunks, one WordArt run looked up and measured in chunks, WordArt glyphs laid out in an envelope; rev 18: pieces with no glyphs on one line; many pictures; shadowed elements; fixed-step clock; the stop comes before the element's end by the diagnostics counters, WordArt runs and glyphs built included) | red. Mutations: each checkpoint removed (the picture one does not exist: its removal would be equivalent); a line drawn in one call; the time never compared. |
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
| AC6 | `PdfLimitTests.APdfOverItsBytes_IsAbortedWithinTheBound` (injected limit; crossed between elements, at a page end and at the close) + `CountedLimitTests` row for PNG bytes (refused before the page is written) + opt-in `CalibrationTests.EachHostileClass_IsStoppedWithinItsBound` and `CountedClasses_AreRefusedAtOnce` (each class above and WI-004's hostile builder, the PDF classes built as many pages that each end within the machine's memory, so that the meter's stop comes before a page's end could exhaust it; the assertion is the metered drawing time against `MaxDrawTime`, not wall-clock time or peak memory, which include page ends AC6 leaves unbounded; the longest step of each class, text and WordArt measured by the step theory below; and, for the page-end class, recorded and not asserted: memory, bytes written, end time and peak, per second of drawing, on pages built to end within the machine) | red. Mutations: count checked only at close; PNG bytes counted after writing. |
| AC6 | opt-in `CalibrationTests.TextAndWordArtSteps_AtTheElementLimits_AreShort` (rev 16; rev 17: ten shapes of text and WordArt at their element limits, words and WordArt of distinct glyphs at 4 and 250 pt, a box of 8,175 words just over one chunk, and (rev 19) a word of 8,388,608 units that is not valid UTF-16 included, under `RenderLimits.Default`, each drawn to the end; each row matched by its exact name; rev 18: each row's element checked against the family and size of every run, the UTF-16 units and the distinct characters its name states, then a counter of its shape after drawing (distinct glyphs measured, for the words) and no font substituted; each longest step under 2 s) | Red at rev 17, on rev 16's code: each new row failed its shape counter, and its steps were 23.5 s (the 250 pt word), 4.9 s (the 4 pt word), 1.5 s and 4.9 s (the distinct WordArt), three of them over the 2 s bound. Mutation: the checkpoint every 1,024 characters of a run removed (killed). Survive by design, pinned elsewhere: the checkpoint every 1,024 words removed (gathering words in `Wrap` is cheap: the line's longest step stays near 142 ms; `MeterTests`' row "words of one line" pins it); the round 4 review's V4 (one line's metrics read from every piece's font: 777–812 ms, and the 2 s bound catches a step of seconds, not one a few times slower). Killed: the round 4 review's V5 (`DrawLine` re-measuring every piece: a step of 17.3 s on the 250 pt distinct word); the round 5 review's R5-V4 and R5-V5 (rows drawing another row's text, or at another size: rev 18's checks of what each row builds). |
| AC3, AC9 | `ChunkedTextTests.ALongPiece_MeasuredInChunks_IsItsWholeWidth_BitForBit`, `ALongPiece_LookedUpAndPositionedInChunks_IsTheWholePiece_BitForBit`, `AWordArtRun_LookedUpAndMeasuredInChunks_IsTheWholeRun_BitForBit` (rev 17: letters, ideographs, supplementary ideographs and a surrogate pair across the first boundary; 4 to 4,000 pt, 256 and the next float above it), `ALongPiece_MeasuresEachDistinctGlyphOnce`, `AShortPiece_IsMeasuredAndPositionedWhole`, `Chunks_AreAtMost1024Units_AndNeverSplitAPair`, and two pixel rows of two chunks each; rev 18: `ALongPieceThatIsNotValidUtf16_IsWhatTheWholePieceCallsGive` (an unpaired surrogate in the second chunk, at the first boundary, at the start, at the end; 10 and 300 pt; width, glyphs, positions, WordArt widths and pixels), `ManyLongPieces_ShareOneTableOfAdvances` (an allocation bound: under 64 KB a piece), `LongPiecesInOneContext_EachUseTheirOwnFontsAdvances`, and `Chunks`' row "a high half at the end"; rev 19: `TheSharedTable_ForgetsOnlyTheLastPiecesGlyphs` (`Remembered` after each of three pieces); rev 20: `TheSharedTable_ForgetsAPieceWhoseMeasuringFailed` (a table begun with no font throws while measuring; the next piece is exact) | Mutations killed: the canonical strike never used; each chunk scaled; a glyph placed after its advance; a pair split; the whole-piece threshold; the table measuring a glyph again; the canonical strike aliased (the round 5 review's V2); rev 18: invalid text not recognised (in each of the three places), a table per call, a table that does not forget the last piece's glyphs or keeps its first font, `Chunks`' guard at the text's end removed (R5-V1); rev 19: the table's list of met glyphs never cleared (the round 6 review's M4), the invalid result's width not 0, its glyphs not empty, in each helper; rev 20: a glyph listed as met only after it is measured (the round 6 review's M5, by the failure-path test), and the round 7 review's: the width −0f, the table forgetting only on a new font, the list of met glyphs growing with the piece, the list to measure never cleared. Survive by design, held by reading: the whole-piece call restored for invalid text (the round 7 review's M1 to M4; the step row records 5–7 ms against 34–37 ms), and the table cleared whole at each piece (its M7: the same results at a constant cost). Equivalent on this machine, so not killed: the canonical strike at 256 rather than above it, and hinted, with linear metrics, or not positioned to the subpixel (R5-V3, R5-V9): at 256 the 64-unit advances times 4 are the font's own, and these settings do not move DirectWrite's advances. |
| AC7 | `PdfLimitTests.AStoppedPdf_IsAborted_NotClosed` (no `%%EOF`, no font object after the stop; a stopped context's next `ToPdf` writes 0 bytes) | red. Mutation: no `Abort()`. |
| AC7 | CLI `RenderLimitCliTests.ARefusedCommand_WritesNothing`, `AStoppedCommand_WritesNothing` (injected clock), `TwoOutputsWithOnePath_IsAUsageError`, `AFailedReplacement_PutsEveryOutputBack` (three outputs; a move into place fails after another move into place succeeded, through the file-operation seam; the seam's calls show the undo in reverse order; no temporary remains), `ABackupThatCannotBeRestored_IsKeptAndNamed` (the seam fails one restore), `ASuccessfulCommand_LeavesNoBackupOrTemporary`, `ALeftoverBackup_SurvivesTheNextRun`, `AnOverLimitCommand_IsRefusedBeforeItsOutputsArePlanned` (rev 22: over the page limit, PNG to a folder that does not exist: refused by the limit, not the folder), `AnOverLimitCommand_IsRefusedBeforeItsOutputsAreNamed` and `AnOutputWhoseNamingFails_IsReported_WhenTheCommandIsWithinTheLimits` (rev 23: PNG and PDF, an output path holding a NUL character, which fails as it is named: over the page limit, refused by the limit with no word of the path; within the limits, the path's own error) | red. Mutations: PNG pages written as drawn; no backup; backups not restored; restored in forward order; duplicate paths accepted; backups never deleted; rev 22: the CLI's counted check removed (the folder error comes first); rev 23, the round 10 review's: the outputs named, their paths compared and their folders checked before the check (`V10-M1`), the outputs named before the check and compared after it (`V10-M2`), the CLI's check told there is no PNG output (`V10-M4`). Equivalent: the check given a context of its own (`V10-M5`). |
| AC8 | `CountedLimitTests.Defaults_ArePinned` | Mutation: any default changed. |
| AC9 | Real pages (maintainer's check, as WI-005's); opt-in gate; all suites; rev 23: `CliTests.MultiPagePng_NamesItsPagesFromTheNameWindowsWrites` (the one change of name, on Windows: `sheet.png.` gives `sheet-p1.png` and `sheet-p2.png`) | Rev 23: the pages named from the path as given (the round 10 review's `V10-M3`). |

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
   against AC5. Done 2026-10-02 (Measurements, "Task 1"); the machine was not quiet, which the margin absorbs.
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

Tasks 2 to 8 were done on 2026-10-02 (Results).

## Results (2026-10-02)

**Tests, red first.** Each named test failed against a stub before the change that turned it green; the
unseekable-stream test was shown red in its own process, since the code before WI-006 ended the process there (exit
139). All suites pass: Core 33, CLI 53, PubReader 395 (3 opt-in skipped), Rendering 632 (8 opt-in calibration tests
skipped). Mutations: 267 (147 across Tasks 2 to 7, 25 for the review's round 1, 9 for round 2, 9 for round 3, 19 for
round 4: the review's six and 13 more; 14 for round 5: the review's seven and seven more; 18 for round 6: the review's
15 and three more; 10 for round 7: the review's; 7 for round 8: the review's three and four more; 4 for round 9: the
review's; 5 for round 10: the review's), each run against the tests that name it, every one killed but sixteen, each a
survivor by design or an equivalent:
- Four run against the opt-in step theory and survive it by design, while an ordinary test kills the same mutant or the
  theory records the mechanism: `R3-C2` (the checkpoint every 1,024 words removed; `MeterTests`), the round 4 review's
  `V4` (one line's metrics read from every piece's font: 777–812 ms, under the theory's 2 s), the round 6 review's `M4`
  (the table's list of met glyphs never cleared; `TheSharedTable_ForgetsOnlyTheLastPiecesGlyphs`) and the round 7
  review's `M4` (the whole-piece calls restored for invalid text: 34–37 ms against 5–7 ms, held by reading).
- Four more survive by design against the ordinary tests: the round 7 review's `M1`, `M2` and `M3` (the whole-piece call
  restored in one helper each: the same results, a step the theory alone records) and its `M7` (the table cleared whole
  at each piece: the same results at a constant cost a piece).
- Four settings of the canonical 64-unit strike are equivalent on this machine (DirectWrite): the strike used at exactly
  256 units rather than above (the 64-unit advances times 4 are the font's own), hinted, with linear metrics, or not
  positioned to the subpixel (`R4-canonical-ge`, `R4-canonical-strike-hinted`, the round 5 review's `V3` and `V9`).
  All are kept as Skia sets its canonical strike; a FreeType build is where they would matter (UNVERIFIED).
- The round 9 review's `V9-M2` (the refusing paragraph's own runs walked before the refusal) survives by design: it
  walks one paragraph more, inside the stated bound of the runs limit plus one paragraph.
- The round 10 review's `V10-M5` (the CLI's check given a context of its own) is equivalent, as it said: the check
  charges nothing to its context and adds no warning.
- Two of the round 6 review's are equivalent, as it said: `Invalid`'s length clause alone (no chunk is empty) and
  `Chunks`' `length > 1` alone (a chunk of one unit is always the text's last). Its `M5` (a glyph added to the table's
  list after it is measured), which rev 19 called unobservable, is killed since rev 20 by
  `TheSharedTable_ForgetsAPieceWhoseMeasuringFailed`.

After round 2's fixes, the 94 on the files it changed were run again. After round 3's, the 48 named against the two
test classes it changed. After round 4's, the 46 on `TextRenderer` and `WordArt` and the round's own. After round 5's,
the 57 on those two files and the round's own, and the six timing mutants against the step theory. After round 6's, the
73 and the seven timing mutants. After round 7's, the 81 and the nine. After round 8's, all 258 and the nine. After
round 9's, the 81 on `CliApp` and `RenderContext` and the round's own (no rendering code changed in that layer). After
round 10's, the 24 on `CliApp`, the round's five among them (no production code changed in that layer). The
hand-off lists each mutation with the tests that killed it. The generated soundness
test drew 5,000 variants: 1,535 passed through, 1,745 counted as decoded, 1,720 unreadable, none unsound.

**Coverage** (all suites, merged per file, after the review's round 10): `RenderLimits` 99.0 %, `RenderContext` 100 %,
`Exporter` 100 %, `PageRenderer` 100 %, `TextRenderer` 100 %, `WordArt` 100 %, `FontResolver` 95.8 %, `PictureCache`
97.0 %, `JpegWalk` 100 %, `CountingStream` 100 %, `DocumentJson` 90.6 %, `CliApp` 99.5 %.

**Calibration** (opt-in, the Task 1 machine and state). Ordinary documents draw in at most 14.9 s (the catalogue: 2.5 %
of `MaxDrawTime`), the real files in at most 0.80 s each. The hostile classes ran with `MaxDrawTime` injected at 20 s,
since the bound is `MaxDrawTime` plus the longest step (the ranges include the re-runs at rev 16, after the text rows
moved to their own theory, and at revs 17 to 21):

| Class | Result | Longest step |
|---|---|---|
| Shadowed 1 × 1 pt shapes with 1,000 pt translucent lines (PNG 300 dpi) | stopped at 20.0 s | 32–43 ms |
| A 1 MP picture page-sized, many times (PNG 300 dpi) | stopped at 20.1–20.2 s | 175–352 ms |
| Two 56 MP pictures larger than the cache together, alternately (PNG 150 dpi) | stopped at 20.0–20.2 s | 286–367 ms |
| One line of 200,000 stacked U+0336 at 13,000 pt, each over the whole page (PNG 300 dpi) | stopped at 20.0 s | 3–10 ms |
| WordArt of 16,384 glyphs with a 1,000 pt outline (PNG 300 dpi) | stopped at 20.0 s | 5–49 ms |
| Page-sized translucent shapes with layered shadows (PNG 300 dpi) | stopped at 20.0 s | 37–50 ms |
| Distinct crops of a 4 MP picture (PDF) | stopped at 20.0–20.1 s | 118–162 ms |
| Page-sized translucent shapes with layered shadows (PDF) | stopped at 20.0 s | 10–24 ms |
| 20,000 pages of WordArt (PDF) | stopped at 20.0 s on page 11,377 to 13,225 (50–52 s wall: the rest is page ends) | 14–22 ms |
| 20,001 pages; 20.5 gigapixels of PNG; 40 installed families in one PDF; 5,000 font names | refused by the count in at most 31 ms (through the library; through the CLI, since rev 22, before its outputs are planned: a million pages to PNG refused in 1.0 s, process start and loading included) | none |

A 225 MP JPEG passed through in each of round 8's eight placements took 2–6 ms to embed, and the process's peak
working set did not grow. The PDF page's end, recorded for WI-007: pages of WordArt wrote about 21–24 MiB and spent
1.57 s outside drawing (page ends) per second of drawing.

**A command's worst case (AC6)**, from these measurements:
- loading: at most `MaxLoadBytes` (256 MiB; Task 0 measured a 256 MiB load peaking at 2.3 GB); for a `.pub`, in place
  of loading, its import within the import's own limits (`PubImporter.MaxElements` units placed, one at least for each
  page; `PubImporter.MaxTextPlaced` characters placed; `PubPackage.MaxStreamBytes` for each stream read) and the
  import's write to a temporary folder: the document's JSON (at most `PubImporter.MaxJsonBytes`, 256 MiB), its
  pictures, and its report (one entry per issue, each issue costing a `MaxElements` unit), all before the counted check
  (rev 23; rev 24: in place of loading, and the report);
- the counted check: at most 16,384 font resolutions, and a walk over every paragraph and run of the document for its
  font names, bounded by the load limit (the round 8 review measured 3.1 s for a 268 MB document of 89 million empty
  paragraphs, in Release, after 23 s of loading); a PDF command's check also walks the document for its typefaces, and
  `ToPdf` walks it again (the round 9 review measured, for a text box of 89 million empty paragraphs at the load limit,
  in Release: a PNG command's check 2.7–2.8 s, a PDF command's 4.1–4.2 s with both walks, and `ToPdf`'s walk
  1.4–1.5 s);
- the CLI's planning of its outputs: after the check, so at most `MaxPages` outputs, each named once (rev 22: before,
  a million-page document to PNG was planned before it was refused, 566.6 s with the output folder in 8.3 short form;
  now it is refused in 1.0 s, process start and loading included); naming a page took the round 10 review 0.3–0.8 µs,
  and 623–639 µs when the full output path holds a `~`, which `Path.GetFullPath` tries to expand as an 8.3 short name,
  looking it up in the file system for each page: 12.5–12.8 s for 20,000 pages, before drawing and outside
  `MaxDrawTime` (rev 23);
- drawing: `MaxDrawTime` (600 s) plus one step. The steps, by kind (rev 14):
  - drawing a picture or a layer onto the page grows with the page's pixels: 352 ms at most measured (a 1 MP picture
    drawn page-sized at 300 dpi); up to `MaxPixels` (500 MP, about 60 times a Letter page at 300 dpi) about 21 s,
    extrapolated, not measured;
  - a picture's decode grows with the picture's pixels: up to `MaxPicturePixels` (250 MP) for a bitmap, and up to
    `MaxPdfPicturePixels` (64 MP) for a PDF, where the review measured 5.27 s for a 64 MP CMYK JPEG (the class of two
    56 MP pictures, decoded at each draw and drawn page-sized at 150 dpi, measured 286–367 ms a step);
  - laying out and drawing text and WordArt is in steps of at most (rev 17): 1,024 characters scanned, measuring at most
    2,048 UTF-16 units' glyphs (the characters scanned, and the start of a short word that ends among them); 1,024
    UTF-16 units of a longer piece looked up, measured and added or placed; 1,024 words gathered into lines; one run of
    64 glyphs drawn (one glyph above 256 device pixels); 1,024 UTF-16 units of a WordArt run looked up and measured; or
    one WordArt glyph built, laid out, placed or painted. A step's time grows with the distinct glyphs it meets and
    their size, which each chunk bounds. A piece that is not valid UTF-16 costs its chunks up to the first invalid one,
    and lists sized for the whole piece, and draws nothing (rev 19; before it, the whole-piece call scanned the piece in one step: 35–40 ms at 8,388,608
    units in the round 6 review's Release probe). Two kinds grow with an element limit: a line's pieces are scanned for its
    metrics as it is laid out, and its words summed before it is drawn (up to 4,194,304 words; the round 4 review
    measured 99–103 ms and 120–135 ms); and a WordArt's passes over all its outlines, which dispose of the unplaced
    outlines, join the ink bounds and, after drawing, dispose of the placed ones (up to 16,384 glyphs; the round 4
    review measured 16.5 and 17.3 ms). Three more steps copy or count a whole piece or element, measured by the round 7
    review at the limits in Release (rev 20): a long piece copied out of its run (2.6–5.6 ms at 8,388,608 units); its
    glyph and position lists allocated when it is positioned (about 5 ms) and copied out at the end (11–13 ms, the
    valid 8,388,608-unit word's longest step); a WordArt's characters counted before it is refused (14–18 ms at
    8,388,608). The element's check is one step too: at most the runs limit plus one paragraph walked for runs,
    characters, WordArt glyphs and font names (29–37 ms at 262,144 runs with 256-character names, in Release; rev 21: a
    paragraph without runs counts as a run, refused as counted, so millions of empty paragraphs no longer make it a
    step of seconds). Measured at the element limits by runs drawn to the end
    (`CalibrationTests.TextAndWordArtSteps_AtTheElementLimits_AreShort`: six runs in this machine's Debug test host,
    two at rev 17 and one each at revs 18 to 21; rev 16's code in brackets, from the same rows' red run):

    | Text or WordArt at its element limit | Drawing | Longest step |
    |---|---|---|
    | One line of 4,194,304 words in a very wide box (PNG 72 dpi) | 5.5–6.1 s | 124–177 ms (122 ms; round 3's Release probe measured 219–313 ms) |
    | One word of 8,388,608 characters (PNG 72 dpi) | 0.3–0.4 s | 13–19 ms (90 ms) |
    | One word of 8,388,608 characters cycling 60,000 SimSun-ExtB ideographs, 4 pt (PNG 72 dpi) | 5.2–7.4 s | 53–75 ms (4,915 ms) |
    | The same at 250 pt | 41.5–51.4 s | 222–278 ms (23,536 ms, in 23.8 s of drawing) |
    | One word of 8,388,608 characters, the last an unpaired surrogate (PNG 72 dpi; rev 19) | 0.2–0.3 s | 5–7 ms (the round 6 review measured rev 18's whole-piece scan at 37.2 ms, in Release) |
    | One run of 8,388,608 characters of short words in a 5 pt column (PNG 72 dpi) | 7.2–8.7 s | 23–33 ms (25 ms) |
    | A text box of 8,388,608 characters in 2,048 paragraphs, 4 pt type (PNG 300 dpi) | 2.3–2.9 s | 17–20 ms (20 ms) |
    | A text box of 8,175 words of 1,025 letters, each measured in two chunks (PNG 72 dpi) | 0.4 s since rev 18 (1.1 s at rev 17, with a table a piece; the round 5 review measured rev 16 at 0.3 s) | 7–12 ms (19 ms at rev 17) |
    | A text box of 262,144 empty paragraphs, the runs limit (PNG 72 dpi; rev 21) | 0.2 s | 42 ms |
    | A WordArt of 262,143 empty paragraphs and one run (PNG 72 dpi; rev 21) | 0.1 s | 60 ms (the round 8 review measured 1.4 s at 20 million paragraphs before rev 21) |
    | WordArt of 16,384 complex glyphs in a wave (PNG 72 dpi) | 1.7–1.9 s | 17–18 ms (31 ms) |
    | WordArt of 16,384 distinct Microsoft YaHei ideographs in a wave (PNG 72 dpi) | 5.4–6.6 s | 101–110 ms (1,539 ms) |
    | The same at 250 pt | 8.2–10.4 s | 324–413 ms (4,919 ms) |

    The 250 pt word draws in about twice rev 16's 23.8 s: its measuring and its drawing each meet its 60,000 distinct glyphs
    once, where rev 16 met them once in all, in one step. The test holds each longest step under 2 s, which catches a
    step of seconds, not one a few times slower than measured (the round 4 review's V4, 777–812 ms, passes it; its V5,
    17.3 s on the distinct words, does not);
    the `MeterTests` rows pin each checkpoint. Earlier steps for these shapes: 5.19 s for one run's layout before rev 14's
    checkpoint every 1,024 characters (the review's P2), 1.19 s for WordArt placing before rev 14's placing checkpoints
    (P13), and 1.75 s for one line's metrics read from every piece's font, before rev 15 read them once per run (N1);
- each PDF page's end: not bounded until WI-007 (here 1.57 s per second of drawing for WordArt pages; Task 0 measured
  up to 4.6 times);
- the close: at most 36 typefaces and the document's dictionaries; its bytes measured up to 13.3 MB a typeface, but
  its time is not bounded by a count until WI-007 (rev 14). It grows faster than the distinct glyphs drawn from each
  typeface: one typeface of 2,000 distinct ideographs closed in 0.8 s, of 10,000 in 58.5 s (as long as their
  drawing), and of 20,000 in 22.6 s and 142.9 s in the review's two runs. It is bounded only through `MaxDrawTime`,
  which limits the glyphs drawn (about 5–7 ms each the first time in a PDF): so a command can spend about as long
  again in the close as in drawing, and more for typefaces of many thousand glyphs (estimated, not measured);
- PNG encoding: `MaxPngPixels` at about 50 ns a pixel, about 1,000 s at 20 gigapixels (extrapolated);
- bytes: a PDF at most 2 GiB plus one step's writes, one page's end and the close; PNG at most 16 GiB plus one page;
- memory: each single allocation is bounded by AC4, `MaxPicturePixels` and `MaxPixels`; what SkPDF holds for a page,
  and the total, are WI-007's.

**AC9, real pages.** Every page of the 192 real files (269 pages at 150 dpi) renders pixel-identical to `main`
(0259870's code) in eight of ten comparisons, in both orders, before and after the review's round 1. Two comparisons,
each the first after other heavy work and each rendering `main` first, found 44 and 49 pages differing (worst score
0.031 both times). The fix verification (round 2) showed why that is not WI-006's: rendering varies between processes
at `a914605` too. On generated single-font pages, a series' first process differed from the later ones on 79
(`a914605`) and 151 (rev 14) of 1,792 bitmaps, and every later process agreed with the others and across the two
builds; only hinted TrueType fonts (Arial, Times New Roman, Calibri, Consolas) varied, never Microsoft YaHei or Segoe UI.
Its mechanism lies below Pubsmith and is not established. Nothing in WI-006's code that reaches the pixels depends on
time or state. The real pages show the same: after round 2, `main`'s second process differed from its own first on the
same 49 pages (worst score 0.031) with the same font substitutions in every file, while both renders of rev 15 matched
`main`'s second on all 269 pages. Warm processes can disagree too, though rarely (the round 3 verification saw 5 of 56 generated bitmaps vary between
warm processes of one build, at `a914605` as much as at rev 15). So a real-page comparison counts a page as differing
only when it matches none of two or more warm renders of `main` (none the first process of a series), on a quiet
machine; comparing recorded drawing calls instead, which repeated exactly in every build, separates Pubsmith's layout
from glyph rasterization. The opt-in gate was not run again, because it writes into the maintainers' harvest,
which this work treats as read-only; pixel-identical pages leave its numbers (19/106 pages ≤ 3.0, median 8.63, text
429/432) as they were.

**What implementing changed** (each found by a red test, a surviving mutation or a measurement; blame as the ledger
records it):
- `DocumentJson.Load(path)` also refuses a file already larger than the limit before reading any of it; the limited
  read still stops a file that grows or has no length (TEST-GAP: no test pinned the default limit).
- The JPEG walk gained a row for an overlong segment after the frame header (TEST-GAP: a walk that stopped there was
  not caught).
- `ARefusedContext_RefusesEveryLaterRender` also checks that a refused context counts nothing more (TEST-GAP:
  `RenderBitmap` without its guard still threw, but only from `Render`, after counting).
- The chunked-text rows were rebuilt as round 3's probe built them (TEST-GAP: the first rows drew identical pixels
  for all four variants Design names, whose differences show late in long rotated lines).
- The line checkpoint is pinned by the exact count of clock readings at the stop (TEST-GAP: every line draws a run of
  glyphs after its own checkpoint, so the stop moved by one step only).
- The meter keeps the longest step, for calibration and AC6's worst case (internal).
- The rendering tests reference the reader, to build WI-004's publications as its tests do.
- Calibration: the stacked-marks class needed round 2's placement (each mark's ink over the page); 8,388,608
  characters in small type draw in 2.7 s, so that class is recorded, not stopped.

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

## Implementation review, round 1 (rev 14): how each finding is answered

| # | Finding (short) | Blame | Rev 14 |
|---|---|---|---|
| R1 | The close's stated time (0.75 s a typeface) is false: it grows with the distinct glyphs drawn | PLAN-GAP | AC6, the Results and the README state it as unbounded by a count until WI-007, with the measurements; carried to WI-007 |
| R2 | Steps outside AC3's checkpoint list: one long run's layout (5.19 s), the layout pass's lines, WordArt placing (1.19 s); a picture's decode is a step that grows with the picture | AC-GAP | AC3 amended: a checkpoint every 1,024 characters of a run, before each line laid out, before each WordArt glyph placed; `MeterTests` rows for each; the Results list the step kinds |
| R3 | `ToPdf`'s own font-name refusal was never hit | TEST-GAP | `ToPdf_RefusesTooManyFontNames_BeforeWritingOrResolvingThem` |
| R4 | Names were left out of the check-equals-exporters test | TEST-GAP | The generated test has a name limit, names that resolve and that do not, WordArt and empty paragraphs |
| R5 | Stickiness pinned for 3 of 11 limits | TEST-GAP | `EveryRefusal_IsSticky`, a row for each limit (both PDF-bytes sites) |
| R6 | Per-element limits pinned only with one paragraph | TEST-GAP | Rows across two paragraphs, each under the limit |
| R7 | The one-glyph threshold pinned only unrotated | TEST-GAP | Rows at 90° and 45° and under uneven scales |
| R8 | A layer wholly off the page unpinned | TEST-GAP | `APdfLayerWhollyOffThePage_IsDrawn` |
| R9 | The CLI's undo on access denied unpinned | TEST-GAP | `AFailedReplacement_WithAccessDenied_PutsEveryOutputBack` |
| R10 | A new output that cannot be removed was left silently, under a message saying every output was put back | AC-GAP | AC7 amended; the error names it; `ANewOutputThatCannotBeRemoved_IsNamed` |
| R11 | The README's advice to delete leftovers could delete the only copy of an output | HOUSE-RULE | The README says which leftover holds what |
| R12 | "The PDF writer decodes any other picture" and two comments | HOUSE-RULE | "counts as decoded" |
| R13 | "Every later render throws it again" read as including `Check` | HOUSE-RULE | AC1 and the README say a refusal by `Check` does not mark the context |
| R14 | A doc comment displaced in `DocumentJson.cs` | HOUSE-RULE | Each stream has its own |
| R15 | Bookkeeping branches unpinned: the counting stream left on the context, `>=` at a checkpoint, a restore after any failure, a file that grows while read | TEST-GAP | Rows for the first three; the grow-while-read branch removed (a file is read for exactly its length, never over the cap) |

## Fix verification, round 2 (rev 15): how each finding is answered

| # | Finding (short) | Blame | Rev 15 |
|---|---|---|---|
| R2 (partial) | One line was still one step growing with its words (1.75 s at the element limit); AC6 still said steps grow with the page's pixels | AC-GAP | N1 below |
| R12 (partial) | The README's limits table still said "a picture a PDF decodes" | HOUSE-RULE | N6 below |
| N1 | One line is one step: its words gathered, and two font metrics read for every piece | AC-GAP | AC3: a checkpoint every 1,024 words as a paragraph's lines are gathered; a line's metrics read once per run (the same maxima, the same pixels); AC6 states the steps by kind; `MeterTests` row "words of one line"; calibration: 31 ms at 4,194,304 words (rev 17: that row drew another row's shape, a switch arm matching its name's prefix first; the line drawn to the end, 126–145 ms: X3) |
| N2 | The undo's new branches partly unpinned (the backup kept beside an unremovable new output; access denied in the undo) | TEST-GAP | CLI `ANewOutputThatCannotBeRemoved_OverAnEarlierOne_KeepsAndNamesTheEarliersBackup`, `AnUndoDeniedAccess_NamesWhatItLeft` |
| N3 | Three refusal sites without a stickiness pin | TEST-GAP | `EveryRefusal_IsSticky` rows: font names in a PDF's typeface pass, pages of a bitmap, a layer that is not finite; each later render is asked on a caller's canvas first, which counts nothing, so another site's own refusal cannot stand in for stickiness |
| N4 | AC9's Results claimed each renderer repeats itself and blamed font availability | PLAN-GAP | The Results state the variation at `a914605`, with the verification's numbers, and how comparisons count a difference |
| N5 | A stop while placing WordArt on a path left its placed paths to the finalizer | AC-GAP | `WordArt.OnPath` disposes them, as `Envelope` does (a code-reading fix: no test can see a disposal without a seam) |
| N6 | The README's limits table, R12's leftover | HOUSE-RULE | "for a picture that counts as decoded in a PDF" |
| N7 | AC7's last sentence and `Commit`'s summary not updated; counts in rev 14's note and the hand-off | PLAN-GAP | AC7 and the summary say "except what its error names"; the counts corrected |

## Fix verification, round 3 (rev 16): how each finding is answered

| # | Finding (short) | Blame | Rev 16 |
|---|---|---|---|
| Q1 | One line is still a step that grows with its words (`DrawLine`'s sums, about 265 ms at the element limit); a long word is measured whole; rev 15's "31 ms" did not measure the line (rev 16 said a run stopped before it; rev 17: the row drew another shape, and the "55 ms" came from a completed run, X3) | AC-GAP | AC6 and the step inventory name both kinds that grow with an element limit, and its numbers come from runs drawn to the end under the default limits (`TextAndWordArtSteps_AtTheElementLimits_AreShort`, which holds each step under 2 s); the hostile-class table no longer carries these rows. No code change: these steps are bounded by the element limits, and the rounds kept finding residuals in the stated numbers, not in the bound (rev 17: not enough; the code changed, X1) |
| Q2 | The line-metrics pin was narrower than its claim (four mutations survived) | TEST-GAP | `ALineIsAsTallAsItsTallestRun_WhereverThatRunStands` is a theory: the tall run in the first, a middle and the last word, and on a second line; the baseline is checked against the font's own spacing and descent |
| Q3 | AC9's rule assumed warm renders repeat; they sometimes do not | PLAN-GAP | The rule compares against a set of two or more warm renders of `main`, or against recorded drawing calls |
| Q4 | "a PDF decodes" in the warning users see and one test header | HOUSE-RULE | "allowed for a picture that counts as decoded in a PDF"; `ThePdfPictureCap_IsAVerdictForThatPdfOnly` now asserts the warning's text, as the file-cap test asserts its own |

## Fix verification, round 4 (rev 17): how each finding is answered

| # | Finding (short) | Blame | Rev 17 |
|---|---|---|---|
| X1 | Measuring a word or a WordArt run cost time per distinct glyph, more at larger sizes, in one step: 22 s for one word at its element limit at 250 pt; a WordArt run measured whole; `Envelope`'s first loop over every glyph. The stated inventory was wrong a third time (N1, Q1, X1) | AC-GAP | Code: a word longer than 1,024 UTF-16 units is measured and positioned a chunk at a time, each distinct glyph measured once, bit for bit the font's whole-word width and positions (Design); a WordArt run is looked up and measured in chunks; a checkpoint before each chunk after the first and before each WordArt glyph laid out (AC3). `MeterTests` rows for each checkpoint; bit-for-bit theories from 4 to 4,000 pt; the step theory gains distinct-glyph rows at 4 and 250 pt (on rev 16's code: steps of 23.5, 4.9, 1.5 and 4.9 s; now 252–260, 60–61, 101–109 and 356 ms). AC6 and the inventory restated by kind, with the two kinds that grow with an element limit |
| X2 | A line's metrics carried into the next line passed every test (the review's V1) | TEST-GAP | The line-metrics theory gains the reverse row: the tall run on line 1 and small runs on line 2, whose baseline comes from the small font's own metrics |
| X3 | Rev 16's account of rev 15's numbers was false: two rows drew other rows' shapes (a switch arm matched a prefix of their names first), and two had completed; the new theory's catch-all arm could mis-route a row the same way | PLAN-GAP | Both calibration switches match exact names and throw on any other; each step-theory row checks a counter of its own shape and that no font was substituted; the rev 16 note, the N1 and Q1 rows, the Results and AC6's test-plan row corrected |
| X4 | "A picture a PDF would decode" survived in `CrashGuardTests`' class header | HOUSE-RULE | Reworded; so is a test comment in `OrdinaryDocumentTests` that stated the rule as loosely ("which a PDF decodes only if…") |
| X5 | AC6's test-plan row named a test that does not exist | PLAN-GAP | It names `EachHostileClass_IsStoppedWithinItsBound` and `CountedClasses_AreRefusedAtOnce`; the text and WordArt steps belong to the step theory |

## Fix verification, round 5 (rev 18): how each finding is answered

| # | Finding (short) | Blame | Rev 18 |
|---|---|---|---|
| Y1 | "Bit for bit" failed for text that is not valid UTF-16: the whole-piece calls give no glyphs and width 0 for an unpaired surrogate, while the chunks measured and drew the valid ones (a box from 0 to 397 ink pixels; reachable through an import that splits a pair between two runs) | TEST-GAP | A chunk that is not empty but gives no glyphs marks the piece, or the WordArt run, invalid, and the whole-piece call's result is returned (Design); `ALongPieceThatIsNotValidUtf16_IsWhatTheWholePieceCallsGive`, ten rows, width, glyphs, positions, WordArt widths and pixels |
| Y2 | The per-piece tables (four 64 K arrays, 640 KB a long piece) made many long words three to six times slower and allocate 43 times as much (5.6 GB for the box of 8,175 words) | HOUSE-RULE | One table per context, reused: a piece forgets only the last piece's glyphs (Design); `ManyLongPieces_ShareOneTableOfAdvances` bounds the allocation (under 64 KB a piece) and `LongPiecesInOneContext_EachUseTheirOwnFontsAdvances` pins the forgetting and the font |
| Y3 | `Chunks`' guard for the text's last unit was unpinned: without it a text ending in an unpaired high surrogate gives empty chunks forever | TEST-GAP | `Chunks`' row "a high half at the end", taken to a bound so that the mutant fails rather than hangs; and `Chunks` never shortens a chunk of one unit, so no mutant of the end guard can make a chunk empty (the invalid-text theory, which measures such a text, hung under that mutant before this) |
| Y4 | The step theory's shape checks could not tell its new rows apart (a distinct-glyph row drawing the letter word, or a 250 pt row at the default size, passed) | TEST-GAP | Each row states the family and size of its runs, its UTF-16 units and its distinct characters, checked against the element it builds; the word rows also check the distinct glyphs measured |
| Y5 | The bookkeeping called the round 4 review's V5 a survivor (it is killed) and listed two equivalent mutants as killed | PLAN-GAP | AC6's and AC3/AC9's test-plan rows, the Results and the hand-off corrected |
| Y6 | A line of pieces with no glyphs was one step the inventory did not name (444 ms for 4,194,304 such words) | TEST-GAP | A checkpoint before a piece with no glyphs is passed over (AC3); `MeterTests` row "glyphless pieces of one line" |

## Fix verification, round 6 (rev 19): how each finding is answered

| # | Finding (short) | Blame | Rev 19 |
|---|---|---|---|
| Z1 | The shared table's "forgets only the last piece's glyphs" was unpinned: a table that never cleared its list passed every test, right but with a cost growing with the square of the pieces (0.33, 1.04, 3.96 s for 500, 1,000, 2,000 distinct-ideograph words) | TEST-GAP | `GlyphAdvances.Remembered` (what the next `Begin` forgets) and `TheSharedTable_ForgetsOnlyTheLastPiecesGlyphs`: 2,000, then 500, then 1 across three pieces; the review's mutant gives 2,500 at the second |
| Z2 | The fallback for invalid text handed the whole piece to Skia's call, one step of 35–40 ms at 8,388,608 units: a third kind growing with an element limit, unnamed | TEST-GAP | The known result (width 0, no glyphs) is returned without the call; the invalid-text theory still proves it equals Skia's; a step-theory row for the invalid word of 8,388,608 units; the inventory states the piece's cost |
| Z3 | Seven claims in the first commit's message said more than the code: counts "before anything is drawn" (per-element counts come before their element), a canvas left "as it was" (its save count is restored; what was drawn stays), "every refusal is sticky" (`Check`'s are not, by AC1), refusals naming page and element (only when known), "each distinct glyph measured once per piece" (once measuring, once positioning; WordArt has no table), PNG "through a counting stream and an abort" (each page's bytes are counted before it is written), the close "unbounded by a count" (its bytes are bounded; its time is not) | HOUSE-RULE | The message reworded to the plan's terms and the implementation re-committed as one commit (the first commit kept under a local tag for the review's fix layer) |

## Fix verification, round 7 (rev 20): how each finding is answered

| # | Finding (short) | Blame | Rev 20 |
|---|---|---|---|
| AA1 | AC3's clause "without those calls" had no test that can fail: the whole-piece calls restored survive every ordinary test and the step theory (34 ms against 5 ms) | TEST-GAP | The clause moves to Design, stated as held by reading and recorded by the step row; the mutants are listed as survivors by design |
| AA2 | The round 6 review's M5 (a glyph listed as met only after it is measured) was called unobservable; a test that makes measuring fail kills it | TEST-GAP | `TheSharedTable_ForgetsAPieceWhoseMeasuringFailed`: `Begin` with no font, `Learn` throws, the glyphs are still remembered and the next piece is exact; M5 is killed |
| AA3 | The commit message said WordArt runs are "positioned" in chunks (they are looked up and measured so, then placed a glyph at a time); two doc comments said "without the call(s)" without scope (a piece of one chunk makes them) | HOUSE-RULE | Both reworded; the implementation re-committed |
| AA4 | The inventory left out three steps that grow with an element limit, each under 20 ms: a long piece copied out of its run, its lists allocated and copied when positioned, a WordArt's characters counted before refusal; the invalid-piece sentence omitted the lists | PLAN-GAP | AC6 and the inventory name them with the round 7 review's Release measurements; the sentence names the lists |

## Fix verification, round 8 (rev 21): how each finding is answered

| # | Finding (short) | Blame | Rev 21 |
|---|---|---|---|
| AA3 (residual) | The commit message still said "word" where the code chunks per piece (a run's part of a word), and claimed a checkpoint per paragraph for WordArt | HOUSE-RULE | "A long piece of text (a run's part of a word, over 1,024 UTF-16 units)"; "a text box's paragraphs"; re-committed |
| AB1 | A text element of millions of paragraphs without runs made the element's check one step of 3.2 s (text box) or 6.5 s (WordArt) at the load limit: no limit counted such a paragraph, and the check walked them all before refusing | TEST-GAP | A paragraph without runs counts as one run, refused as the paragraphs are counted (AC2); `ElementLimitTests` rows "empty paragraphs", "empty paragraphs, WordArt", "runs and empty paragraphs"; `ManyEmptyParagraphs_AreRefusedAsTheyAreCounted`; two step rows at the runs limit; AC6 names the check's step and the pre-check's walk |
| AB2 | `LookUpAndMeasure`'s comment said "without the calls" without scope; `Glyphs`' said a one-chunk piece makes both calls (an invalid one makes only `GetGlyphs`) | HOUSE-RULE | Both scoped exactly |
| AB3 | The same steps were "a few milliseconds" in one place and 11–18 ms in another; the invalid word's row was 5, 5–7 and 34 or 34–37 ms in different places | PLAN-GAP | "Under 20 ms" and "5–7 ms against 34–37 ms" throughout |

## Fix verification, round 9 (rev 22): how each finding is answered

| # | Finding (short) | Blame | Rev 22 |
|---|---|---|---|
| R9-F1 | The CLI planned every PNG page's output (`GetFullPath` of each page's path) and checked its folder before the counted check refused the page count: 566.6 s for a million tiny pages with the folder in 8.3 short form, 21.4 s and 7 GB at the load limit with long names | TEST-GAP | `RenderLimits.Check` first, then the outputs planned; the PNG path expanded once; `RenderLimitCliTests.AnOverLimitCommand_IsRefusedBeforeItsOutputsArePlanned`; AC7 and AC6's worst case (with the PDF command's two typeface walks); a million pages to PNG now refused in 1.0 s |
| AB3 (residual) | The Results' survivor list still said "34 ms against 5 ms" | PLAN-GAP | "34–37 ms against 5–7 ms" |
| R9-F2 | Rev 21 split the AC2 test-plan row so that the opt-in step rows took the element-limit theory's description; rev 21's mutations were not named | PLAN-GAP | The row describes each test again and names the mutations |
| R9-F3 | The commit message said characters, runs and WordArt glyphs are all "refused as they are counted" (only the runs are) | HOUSE-RULE | "(the runs refused as they are counted)"; re-committed |
| R9-F4 | The runs limit's summary, the README row and the refusal message call the limit "runs", though a paragraph without runs now counts as one | HOUSE-RULE | The summary and the README say so; the message is unchanged (it names the limit), as the review allowed |
| R9-F5 | The in-loop refusal was pinned only for paragraphs without runs (the review's `V9-M1` survived) | TEST-GAP | `ManyOneRunParagraphs_AreRefusedAsTheyAreCounted`; `V9-M1` killed |

## Fix verification, round 10 (rev 23): how each finding is answered

| # | Finding (short) | Blame | Rev 23 |
|---|---|---|---|
| R10-F1 | AC7's new clause ("before it plans its outputs") had no test that can fail: the test saw only which error wins, so naming every output before the check and comparing their paths after it passed every CLI test (the review's `V10-M2`) | TEST-GAP | `AnOverLimitCommand_IsRefusedBeforeItsOutputsAreNamed` (PNG and PDF: an output path holding a NUL character, refused by the limit with no word of the path) and its counterpart within the limits; `V10-M1` and `V10-M2` killed |
| R10-F2 | Fix-introduced: PNG pages are named from the output path once made full, so some names changed (a trailing dot, a trailing space after a name with no extension, an 8.3 short name), though AC9 said nothing else changes | TEST-GAP | The new names kept and named in AC9 (with a path ending in `.` or `..`, found since); `CliTests.MultiPagePng_NamesItsPagesFromTheNameWindowsWrites`; `V10-M3`, a survivor until now, killed |
| R10-F3 | Three of rev 22's sentences were inexact: round 9's 4.1 s is a PDF command's whole check, not its typeface walk; "rev 21's own work held"; "the last '34 ms against 5 ms' corrected" (round 7's row keeps it, as history) | PLAN-GAP | AC6 gives round 9's PDF timings as measured; rev 22's note reworded |
| R10-F4 | AC6's worst case left out a `.pub`'s import and its write before the check, and gave the planning no time (a `~` in the full output path costs a file-system lookup a page) | PLAN-GAP | Both added to AC6's worst case, with the review's measurements |

## Fix verification, round 11 (rev 24): how each finding is answered

| # | Finding (short) | Blame | Rev 24 |
|---|---|---|---|
| R11-F1 | AC6's `.pub` sentence said the import comes before loading (a `.pub` command loads no JSON: it renders the import's own document) and left out the import's report, whose size only its issue count bounds | PLAN-GAP | "In place of loading"; the report named: one entry per issue, each issue costing a `MaxElements` unit |

## Not in scope

Making any class faster; rendering fidelity; a GUI; a PDF page's end, fault containment for the caller's stream,
the warnings cap and the full memory bound (WI-007, `WI-007-pdf-output-and-memory.md`).
