# WI-005: A shadow no longer costs a page-sized layer; a repeated picture is not decoded and embedded at every placement

**Goal:** rendering (`pubsmith render`, `Exporter.ToPng` / `ToPdf`) costs time and output in proportion to what the
page shows. This first increment removes the three measured hot spots below. Bitmap pages look as before; in PDFs, a
shadow becomes an exact vector shape or an element-sized image instead of a page-sized image. Limits that refuse
hostile documents are WI-005's sequel, **WI-006**.

**Why now:** WI-004's review found that a ~100 KB hostile `.pub` inside every import limit would take about 48
minutes to render to PDF, almost all of it shadows. Measuring the renderer found two more costs of the same kind,
one of which already hurts real files.

**Revisions.** Rev 2 answered the Task 0 review (F1–F18): the limits moved to WI-006. Rev 3 answered the rev-2
review (N1–N13). Rev 4 answers the rev-3 review (R1–R10) by **removing** the two features that review found broken
rather than patching them again: crops are drawn as today (no crop-as-clip: R2), and an over-limit picture is a
placeholder in every output (no reduced-scale JPEG decoding: R1, R6), at a limit no real photo reaches (250
megapixels). Pictures larger than the cache budget are simply not cached (drawn as today), so the cache needs no
relation to the limit, and the large-picture 2× decode peak (R8) cannot occur. R3–R5, R7, R9, R10 are amended in place.
Rev 5 folds in the rev-4 review (S1–S7), whose reviewer prototyped rev 4 and measured
each amendment (all suites green; every real page within 0.005 of `3573075`; the gate unchanged): the over-limit
tests judge the cache's verdict or use an injected limit, so a red test cannot crash the test host (S1); only a PDF
canvas is judged at 300 dpi, any other canvas at its own scale (S2); rotated layers (S3); thin-line rows that can fail
(S4); a translucent ellipse's bounded layer may differ slightly (S5); `Exporter` checks `Dispose` before writing (S6);
WI-006's F8 note (S7). After four plan reviews whose last findings came with measured fixes, the next independent
check is the code review. Rev 6 answers the code review's first round (F1–F4, N1–N7), rev 7 its second (R2-F1–F4, R2-N1–N2), rev 8 its
third (R3-N1–N3), rev 9 its fourth (R4-N1–N4), rev 10 its fifth (R5-N1–N3); see "Code review" below. The
reviews are maintainers' notes kept outside this repository; what each one changed is recorded here.

## Measurements (2026-10-01, before this work)

Per element, PNG at 150 dpi and PDF, on a US Letter page (`Exporter`, Release build):

| Element | PNG | PDF | PDF bytes |
|---|---|---|---|
| Rectangle with fill and line | 31 µs | 5 µs | 7 |
| The same, with a shadow | 5,488 µs | 10,893 µs | 2,385 |
| A 1,500 × 1,500 picture placed small | 33,928 µs | 159,591 µs | 6.75 MB, **every placement** |
| Text box of 1,000 characters | 464 µs | 179 µs | 2,847 |
| The same, with a shadow | 5,706 µs | 11,031 µs | 5,260 |
| WordArt (CanUp envelope) of 1,000 characters | 30,613 µs | 90,942 µs | 753 KB |

- A shadow costs ~180× its element in PNG and ~2,000× in PDF: `PageRenderer.DrawShadow` calls `SaveLayer` with no
  bounds, so each shadow allocates and composites a page-sized layer; in a PDF, SkPDF writes it as a page-sized
  (612 × 792) image.
- A picture is decoded from its file on every draw, and the PDF embeds it again for every placement. On the real
  files: a 4-page brochure with 64 picture placements writes a **42 MB** PDF.
- A picture is decoded with no check of its declared size: a PNG of under 100 bytes claiming 100,000 × 100,000 pixels would
  allocate 40 GB.

Real files (192 harvested: 36 real publications and the test files), per file, largest seen: render 1.1 s (PNG
150 dpi), 3.0 s (PNG 300 dpi), 2.8 s (PDF); 64 shadows (175 WordArt shadows in all, 64 of them path warps); 99
picture placements; largest picture 14.4 megapixels; most distinct picture megapixels in one file 21.0.

## Acceptance criteria

| AC | Criterion |
|---|---|
| AC1 | **A shadow no longer costs a page-sized layer.** (a) PNG at 150 dpi: a page of shadowed elements takes at most 4× the same page unshadowed (each the minimum of 5 timed runs after a warm-up), for opaque shapes, translucent shapes, text boxes and small pictures. (b) PDF: a shadowed opaque shape writes no image (a rectangle adds under 64 bytes); every other shadow writes an image no larger than its layer, which is: for a shape or a picture, the frame plus (2 × line width + 1 pt) on every side; for a text box, the frame; for WordArt, its ink box (measured from a 72-dpi raster of the element alone) plus (2 × outline width + 1 pt) on every side; each plus 2 pt of slack, and for a rotated element the bounding box of that rectangle turned by the element's rotation. Today every such image is 612 × 792. (c) WordArt builds its glyph paths once per element, shadow or not. |
| AC2 | **Shadows look as before.** At 150 dpi on a 300 × 200 pt page, each kind of shadow matches the previous algorithm (the element, drawn by the public renderer, through a colour-filtered layer) within ImageComparer score 0.1: opaque shape; outline-only shape; ellipse; rotated opaque shape; translucent fill and line under a translucent shadow; alpha-0 fill with an opaque line; rotated picture; oval-masked picture with a line; picture with transparency; rotated text box; WordArt envelope, arch and circle with outlines; negative-size translucent shape; negative-size picture with a line; negative-size opaque shape with a line; a ¼ pt and a ½ pt line on a rectangle and on an ellipse; an opaque rectangle with a coloured translucent shadow, and an opaque ellipse with Publisher's grey (128, 128, 128) shadow; an opaque fill with a translucent line, on a rectangle and on an ellipse; a shape with neither fill nor line, and one with an alpha-0 fill and no line (both cast nothing); WordArt with an 8 pt outline; and a translucent ellipse with a line, within 0.2 (a bounded layer can differ from a page-sized one at a few edge pixels: measured worst 0.170). Where elements overlap, each shadow is drawn just before its own element, so it falls over the elements before it. The same holds through the public `PageRenderer.Render` on a caller's own raster canvas at 72 and 96 dpi (it is judged at its own scale), also after the same context has made a bitmap or a PDF, whether that exporter returned or threw. The silhouette the renderer chooses for a PDF canvas is as fine as at 300 dpi. |
| AC3 | **Pictures look as before.** Bitmaps draw each picture exactly as today (the crop's source rectangle scaled into the frame, under the oval mask), from the decoded copy: uncropped, cropped, cropped on one side in a fractional frame, cropped with an oval mask and a line, rotated and cropped all match today's drawing within score 0.1; a JPEG with each of the 8 EXIF orientations is drawn as today's encoded-image drawing turns it, in PNG; in a PDF it is embedded turned (orientation 6: a 40 × 60 image for a 60 × 40 stored picture). Every page of the 192 harvested files at 150 dpi stays within 0.1 of the same page rendered by `3573075` (the procedure below). |
| AC4 | **A picture is read once, decoded once while it fits the cache budget, and embedded at most once per PDF per distinct crop.** Within one `RenderContext`: each picture file is read once, so the context does not see later changes to it; a file that could not be read (an I/O error, such as another program's lock) is read again at its next lookup (a placement looks it up once, a shadowed one twice). For bitmap output a picture no larger than the cache budget (default 100,000,000 pixels) is decoded once, on its first bitmap placement, and kept while the decoded pictures fit the budget (least recently used dropped first, before the next decode); a larger picture, or one whose copy cannot be allocated at that moment, is drawn from its encoded image, as today (the copy is tried again at its next bitmap draw). In a PDF each picture is embedded at most once for each distinct crop (a JPEG passes through, not decoded by us). A PDF made after a bitmap render with the same context still passes JPEGs through. A missing or undecodable picture is a placeholder at every placement, and an unreadable one at every placement where it cannot be read, with today's warning codes (`image-missing`, `image-unreadable`; the message of `image-unreadable` says whether the file could not be read or could not be decoded) (kept once per code and message, so two spellings of one path warn twice, as today). |
| AC5 | **A picture declaring more than 250,000,000 pixels (by default) is never decoded or embedded:** its declared size is read from its header first (as a 64-bit number), and it is drawn as the placeholder with the warning `image-too-large` (which says it was not decoded: the file is read), in PNG and in PDF. |
| AC6 | **Nothing else changes:** the real-file gate stays at 19/106 pages ≤ 3.0, median 8.63 (± 0.05), text 429/432; every existing test passes without change (a `RenderContext` that is never disposed releases its pictures when it is collected). |

## Design

- **What a canvas is:** `Exporter.RenderBitmap` and `Exporter.ToPdf` mark the context with the output they are
  drawing (internal: bitmap or PDF; cleared in a `finally`). On a bitmap, a cacheable picture comes from its decoded
  copy. Every other canvas (PDF, or a caller's own canvas through the public `PageRenderer.Render`) draws the encoded
  image, as today, so a PDF embeds it at most once per distinct crop and passes a JPEG through. Only the PDF mark makes the renderer
  judge line widths at 300 dpi; every other canvas is judged at its own scale (from its matrix).
- **Shadow, opaque shape** (no fill or an opaque fill; no line, or an opaque line at least 1.5 device pixels wide at
  the canvas's scale, taken as 300 dpi for a PDF-marked canvas; a fill of alpha 0 counts as no fill), drawn with no layer, the
  frame sorted (negative sizes) first: a rectangle with a fill and a line → the rectangle outset by half the line
  (square corners, the line's mitre join), filled; line only → the line, stroked with the shadow colour; fill only →
  the fill. An ellipse with a fill and a line → the union (`SKPath.Op`) of the fill and the line's outline
  (`GetFillPath` at the canvas's scale, 300 dpi for a PDF-marked canvas); if `Op` fails, the layered way. Thinner lines and every
  other case take the layered way.
- **Shadow, layered:** `SaveLayer(bounds, paint)` (colour filter = shadow colour, alpha = shadow alpha) in the
  element's own coordinates (after the shadow offset and the element's rotation), bounds as AC1b says, the frame
  sorted first; a rotated element's layer is its rotated rectangle (the PDF writes that rectangle's bounding box).
  WordArt builds its final (warped, scaled, placed) glyph paths once per element (inside the element's
  step, so z-order is unchanged) and draws them into the shadow layer, bounded by their union inflated by
  (2 × outline + 1 pt), then as the element. Memory: one element's final paths at a time (about 2.2× today's per
  glyph).
- **Picture cache** (in `RenderContext`): keyed by the resolved full path (`OrdinalIgnoreCase` on Windows,
  `Ordinal` elsewhere, as `ResolvePath` compares). On first use the file is read and its header read (`SKCodec`,
  size as `long`); missing, undecodable or over-limit pictures are remembered, and a file read is not read again (the
  context does not see later changes). An I/O error makes the picture unreadable at that lookup only: it is not
  remembered, so the next lookup reads the file again (another program's lock is not a verdict). An accepted
  entry keeps the
  encoded `SKImage` for the context's life (memory: the referenced files' bytes). A picture within the cache budget
  gets, on its first bitmap use, a decoded copy (`SKImage.ReadPixels` into N32 premultiplied, which applies the EXIF
  orientation; transient peak 2× the copy, so at most 800 MB at the default budget); before decoding, least recently
  used copies are dropped until the new one fits. If the copy cannot be allocated, nothing is remembered and the
  encoded image is drawn, as today.
- **Limits are injectable for tests** (an internal `RenderContext` constructor taking the per-picture limit and the
  cache budget); public defaults are constants pinned by a test. The two are independent.
- **`RenderContext` becomes `IDisposable`.** It owns the cached images (not the `FontResolver`, which holds no native
  state). After `Dispose`, `PageRenderer.Render` and every `Exporter` method that takes the context throw `ObjectDisposedException` (checked
  in `PageRenderer.Render`, so it holds for pages with no pictures, and at the top of each `Exporter` method, so
  nothing is written to the caller's stream first); `Warnings`, `BaseDirectory` and `Fonts` stay
  readable. Not thread-safe: one context per command, on one thread. The CLI disposes it; existing tests that never
  dispose still pass (`SKNativeObject` finalisers release the images; measured by the rev-3 review).

## Test plan (tests before code; red first)

| AC | Test (project: Rendering unless noted) | Red today / mutation it must kill |
|---|---|---|
| AC1a | `ShadowCostTests.ShadowedPages_CostAboutTheirElements_InPng` (theory: 400 opaque rectangles; 400 translucent rectangles; 100 text boxes of 1,000 characters; 100 placements of a 60 × 40 picture; ratio of 5-run minima ≤ 4) | red (measured): 614×, 631×, 62×, 15×. Mutations: translucent shape's layer unbounded; text layer unbounded; picture layer unbounded. |
| AC1b | `ShadowCostTests.AShadowedOpaqueShape_WritesNoImage_InPdf` (500 rectangles with a 1 pt line, which a 72-dpi judgement would layer: < 64 B each, no image; 500 ellipses: no image; 100 rectangles with a ½ pt line, which a 150-dpi judgement would layer: no image) | red: 2,385 B and a 612 × 792 image each. Mutations: opaque shape through a (bounded) layer; a PDF judged at 150 dpi. |
| AC1b | `ShadowCostTests.EveryLayeredShadow_WritesNoMoreThanItsLayer_InPdf` (theory: translucent shape with a 6 pt line, the same rotated 30°, text box, picture of 60 × 40 px in a 120 × 80 pt frame, WordArt envelope, WordArt arch; every image's size ≤ the AC1b bound computed in the test, WordArt's ink box from a 72-dpi raster of the element alone) | red: 612 × 792. Mutations: each layer unbounded; WordArt layer the page. |
| AC1c | `ShadowCostTests.WordArt_BuildsItsGlyphsOnce` (internal glyph-build counter: 1 per element with or without a shadow) | red: 2. Mutation: shadow drawn by a second full draw. |
| AC2 | `ShadowLookTests.EveryKindOfShadow_LooksAsBefore` (the 28 kinds of AC2, thin lines at (60, 50); oracle through the public renderer) + `TheOracle_SeesADifferentShadow` | Mutations: silhouette without the line; fill and line as two shadow fills (double alpha); translucent shape through the silhouette; alpha-0 fill kept; thin lines through the silhouette; the thin-line rule for ellipses only; `GetFillPath` at resolution 1; bounds without the rotation; WordArt frame-only bounds; bounds without the line or outline; frame not sorted (layers or silhouettes); silhouette drawn black (the shadow's colour ignored); the line's alpha ignored; a shape with neither fill nor line casts a shadow; WordArt's layer margin without its outline. |
| AC2 | `ShadowLookTests.ShadowsAreDrawnElementByElement` (every shadow route, each casting onto the opaque shape drawn just before it: an opaque shape's silhouette, and the layers of a translucent shape, a picture, a text box and WordArt) | Mutations: every non-WordArt shadow drawn first, in one pass; and, one route at a time, the shadows of opaque shapes, translucent shapes, pictures, text boxes, WordArt, or pictures and text boxes together drawn first. |
| AC2 | `ShadowLookTests.EveryKindOfShadow_OnACallersOwnCanvas_LooksAsBefore` (theory: the 28 kinds × 72 and 96 dpi through the public `PageRenderer.Render` on an unmarked raster canvas) | Mutation: unmarked canvases judged at 300 dpi (0.213–0.634). |
| AC2 | `ShadowLookTests.AfterAPdf_ACallersCanvas_IsJudgedAtItsOwnScale` (a ½ pt line on an ellipse, on a 72-dpi canvas, after `ToPdf` with the same context) + `RenderContextTests.TheOutputMark_IsClearedWhenAnExporterReturnsOrThrows` (after `RenderBitmap` and `ToPdf`, each returned and thrown; the drawing call that throws has first drawn something as only the exporter's mark draws it, so the mark was set when it threw: the bitmap decoded a picture, and the PDF wrote a picture and drew a ½ pt line's shadow as a silhouette, with no image of its own) | Mutations: `ToPdf` never clears the mark (each test); either exporter clears it on return only, also with a check of every element made before the mark (in the exporter or in `PageRenderer`), a pre-pass drawing every page to a recorder before the mark, the mark set after such a check on each page, or set and cleared for each page with such a check first; the mark never set; `ToPdf` restores it to the bitmap mark. |
| AC2 | `ShadowLookTests.TheEllipseSilhouette_ForAPdfCanvas_IsAsFineAsAt300Dpi` (the renderer's own silhouette helper given a recording canvas, as for a PDF; path area within 0.1 % of the 300-dpi path) | Mutation: PDF scale 1 (+0.325 %). |
| AC3 | `PictureLookTests.EveryPicture_LooksAsBefore` (theory: uncropped, cropped, cropped on one side in a fractional frame, cropped + oval + line, rotated + cropped; oracle: a test copy of today's `DrawImage`) + `TheOracle_SeesACropOffByAFewPixels` | Mutations: crop offset by one source pixel; no oval mask; source rectangle ignored. |
| AC3 | `PictureLookTests.EveryExifOrientation_IsDrawnAsToday` (theory over orientations 1–8: PNG through the cache vs today's encoded-image drawing, score ≤ 0.1), `AnExifRotatedJpeg_IsDrawnTurned` (orientation 6 pixel check) and `AnExifRotatedJpeg_IsEmbeddedTurned_InPdf` (orientation 6 → a 40 × 60 image) | Mutation: decode with `SKCodec.GetPixels` or `SKBitmap.Decode` (ignore orientation). |
| AC4 | `PictureCacheTests.APictureRepeated_IsEmbeddedOnceInThePdf` (50 placements on 5 pages, PDF < 1.5 × one placement's) | red: 50×. Mutation: a new `SKImage` per draw. |
| AC4 | `PictureCacheTests.AJpeg_PassesThroughThePdf_EvenAfterABitmapRender` (a JPEG: one DCTDecode image, decode counter 0; then `RenderBitmap` and `ToPdf` with the same context: still DCTDecode) | Mutations: bitmap mark always on; mark not cleared. |
| AC4 | `PictureCacheTests.APictureRepeated_IsReadAndDecodedOnce` (file-read and decode counters, 50 placements over 5 PNG pages → 1 and 1) | red. Mutations: decode per draw; read per draw. |
| AC4 | `PictureCacheTests.TheLeastRecentlyUsedIsDropped` (injected budget for two; A B A C A → 3 decodes and decoded pixels ≤ budget throughout; A B C A → 4) | Mutations: never evict (pixels over budget); evict the most recent (A B A C A → 4); evict after decoding (over budget during the decode). |
| AC4 | `PictureCacheTests.APictureOverTheBudget_IsDrawnUncached` (injected budget below a picture: decode counter 0, drawn correctly) | Mutation: cached anyway (pixels over budget). |
| AC4 | `PictureCacheTests.Defaults_ArePinned` (limit 250,000,000; budget 100,000,000) | Mutation: either default changed. |
| AC4 | `PictureCacheTests.DifferentPaths_AreDifferentPictures`; `TheSamePathInAnotherCase_IsOnePicture` (Windows only) | Mutations: key by file name; case-sensitive key on Windows. |
| AC4 | `PictureCacheTests.AMissingPicture_IsAPlaceholderEverywhere_ReadOnce_AndWarnsAsToday` (50 placements of one spelling → 1 warning, 1 read attempt; two spellings → 2 warnings) | Mutations: negative result not cached; warning only on entry creation (two spellings → 1). |
| AC4 | `PictureCacheTests.APictureThatCannotBeRead_IsUnreadable` (a file opened exclusively by the test while rendering: placeholder, one `image-unreadable` warning saying it could not be read) | red (rev 7): "could not be decoded". Mutations: an I/O error escapes the render; a read failure reported as "could not be decoded". |
| AC4 | `PictureCacheTests.APictureThatCouldNotBeRead_IsReadAgainAtItsNextPlacement` (a shadowed placement, so two lookups a page; locked over ten pages: the placeholder, 20 reads; then free over two pages: the picture, 21 reads in all) | red (rev 6): the placeholder again, 1 read. Mutations: a read failure remembered; retried only once; given up after 5 or 20 failures. |
| AC4 | `PictureCacheTests.EveryVerdictButAReadFailure_IsRemembered_SoTheFileIsReadOnce` (theory: a picture drawn, missing, undecodable, over the limit; 3 pages of 5 shadowed placements: 1 read, and exactly the expected warning text, or none) | Mutations: a missing, undecodable, over-limit or drawn picture read again; an undecodable picture reported as "could not be read". |
| AC4 | `PictureCacheTests.ACopyThatCannotBeAllocated_IsNotMade_AndIsTriedAgain` (1,000,000 × 1,000,000 declared, under limits raised for the test; judged without drawing: no copy, twice, nothing remembered, no throw) | red: "Unable to allocate pixels for the bitmap." Mutations: the allocation failure remembered; an allocation that throws; the failure counted as a decode, or its pixels counted. |
| AC5 | `PictureCacheTests.ALiarHeader_IsJudgedTooLarge_WithoutDecoding` (57-byte PNG header, 100,000 × 100,000: the cache's verdict is too-large, decode counter 0; nothing is drawn, so a red run cannot reach a fatal allocation) | red. Mutation: checked after decoding. |
| AC5 | `PictureCacheTests.ASizeThatOverflowsAnInt_IsJudgedTooLarge` (header 65,536 × 65,536, whose `int` product is 0; verdict only) | Mutation: `int` product. |
| AC5 | `PictureCacheTests.AnOverLimitPicture_IsAPlaceholder_InPngAndPdf` (injected limit 1,000 pixels, a real 60 × 40 picture: placeholder and `image-too-large`, with its exact text, in PNG; no image in the PDF) | Mutations: checked for PNG only (the PDF then shows a 60 × 40 image); the message says the file "was not opened". |
| AC5 | `PictureCacheTests.AtTheLimit_IsDrawn_OneMoreIsNot` (injected limit; a real picture of exactly the limit vs one pixel over) | Mutation: `>=` for `>`. |
| AC4 | `RenderContextTests.Dispose_ReleasesThePictures_AndLaterRenderingThrows` (a page with no pictures: `PageRenderer.Render`, `RenderBitmap`, `ToPng` and `ToPdf` throw, and the PDF stream is still empty; `Warnings` still readable) | Mutations: cached images not disposed; guard only in the picture cache; `ToPdf` checks only after writing. |
| AC3 | Real pages (a maintainer's check, recorded in Results; it needs the private harvest of real files, so its small tool is kept outside this repository): every page of the 192 harvested files is rendered at 150 dpi by `3573075` (baseline taken 2026-10-01, 269 pages; a re-render of unchanged code scored 0.000 on every page) and by this branch, each pair is scored with `ImageComparer`, and the worst score is reported; the bound is 0.1 | — |
| AC6 | Opt-in corpus suite (`PUBSMITH_CORPUS`): gate and text unchanged; all existing suites green | — |

Interface coverage (new or changed members → tests): `RenderContext.Dispose` → `RenderContextTests`;
`RenderWarning.ImageTooLarge` → `AnOverLimitPicture_IsAPlaceholder_InPngAndPdf`; `RenderWarning.ImageUnreadable`'s two
messages → `APictureThatCannotBeRead_IsUnreadable` and `EveryVerdictButAReadFailure_…`; the picture cache (internal: get,
read and decode counters, budget, injected limits) → `PictureCacheTests`; the bitmap mark (internal) →
`AJpeg_PassesThroughThePdf_…` and `APictureRepeated_IsReadAndDecodedOnce`; the PDF mark and both marks' restore
(internal) → `AfterAPdf_ACallersCanvas_IsJudgedAtItsOwnScale` and `TheOutputMark_IsClearedWhenAnExporterReturnsOrThrows`; `PageRenderer`'s silhouette and layer-bounds helpers (internal) →
`ShadowLookTests`, `ShadowCostTests`; `WordArt`'s single glyph build and counter (internal) →
`WordArt_BuildsItsGlyphsOnce`. Every touched file ≥ 85 % lines (cobertura, merged per file).

## Tasks (each names the tests it turns green)

0. **Test-plan review gate:** rev 1–3 AMEND; rev 4 reviewed again before production code.
1. Look oracles first (`ShadowLookTests`, `PictureLookTests`, green on today's code: they pin the look). Done; rev 4
   adds the new rows (negative-size opaque, thin lines, one-side crop, 8 orientations).
2. Shadows: silhouettes and bounded layers (`ShadowCostTests` 1a, 1b; `ShadowLookTests`).
3. WordArt single glyph build (`WordArt_BuildsItsGlyphsOnce`; WordArt rows).
4. Picture cache, bitmap mark, header check, `RenderContext.Dispose` (`PictureCacheTests`, `PictureLookTests`,
   `RenderContextTests`); the CLI disposes its context.
5. Measurements again (the table above; every real page vs `3573075`), corpus suite, results here.
6. Documents: README (the library disposes its `RenderContext`; one per command, one thread), WI-001 interface table
   (`RenderContext` is `IDisposable`), WI-004's "Rendering is not budgeted yet" sentence (what WI-005 changed, and
   what still costs; limits are WI-006).
7. Code review, rounds 1 to 5 (revs 6 to 10): the tests first, then the fixes, listed under "Code review"; the
   battery of mutations, the suites, coverage and the real pages again.

## Results (2026-10-01)

Per element, after (same probe and page as the table above, Release build):

| Element | PNG | PDF | PDF bytes |
|---|---|---|---|
| Rectangle with fill and line | 30 µs | 5 µs | 7 |
| The same, with a shadow | **33 µs** (was 5,488) | **3 µs** (was 10,893) | **9** (was 2,385) |
| A 1,500 × 1,500 picture placed small | **624 µs** (was 33,928) | **554 µs** (was 159,591) | **34 KB** (was 6.75 MB): embedded once |
| Text box of 1,000 characters | 428 µs | 155 µs | 2,847 |
| The same, with a shadow | **427 µs** (was 5,706) | **399 µs** (was 11,031) | 3,369 (was 5,260) |
| WordArt (CanUp envelope) of 1,000 characters | 33,980 µs | 87,368 µs | 753 KB (unchanged: no shadow) |

On the 192 harvested files (a maintainer's timing tool over the private harvest, kept outside this repository: each
file imported, then rendered to PNG at 150 and 300 dpi and to PDF; `3573075` and this branch run back to back, twice,
Release build; a range is the two runs):

| | Before | After |
|---|---|---|
| All their PDFs | 268.1 MB | **160.6 MB** |
| Largest PDF (a 4-page brochure: 64 shadows, 64 picture placements) | 41.6 MB | **11.4 MB** |
| PDF time, all files | 17.6–17.9 s | **7.9–8.0 s** |
| PNG time, all files: 150 dpi / 300 dpi | 19.9–20.1 s / 67.2–68.5 s | 17.8–17.9 s / 62.1–62.7 s |
| The brochure: PDF / PNG 150 dpi / PNG 300 dpi (before, the slowest file in every format) | 2.9–3.0 s / 1.1–1.2 s / 3.2–3.3 s | **0.9–1.0 s / 0.5–0.6 s / 1.5–1.6 s** |
| The slowest PNG file after (5 pages, 9 shadows, 5 pictures): PDF / PNG 150 dpi / PNG 300 dpi | 0.4 s / 1.0 s / 3.0–3.1 s | 0.3 s / 0.9 s / 2.8 s |

- **Look (AC2, AC3):** every page of the 192 files at 150 dpi scores at most **0.005** against `3573075` (269
  pages; bound 0.1). The look tests (shadows: 28 kinds, each also on a caller's own canvas at 72 and 96 dpi, and
  overlapping elements; pictures: 5 kinds and 8 EXIF orientations, PNG and PDF) pass.
- **Gate (AC6):** unchanged: 19/106 pages ≤ 3.0, median 8.63; text 429/432 (run on the round-1 code; no later revision changes the drawing of a readable picture, and every real page still scores at most 0.005).
- **Tests:** Core 30, CLI 32, PubReader 395 (+3 opt-in), Rendering 266.
- **Mutations:** 92, each named with the test that must kill it: shadow routes, bounds and colour; thin and translucent lines; invisible shapes; z-order on each shadow route; the PDF and caller-canvas scales; both output marks, set before drawing and restored on return and on a throw, wherever a check before drawing stands; WordArt's single build; the picture look (crop, mask, source rectangle, orientation); the cache's reads, each verdict remembered and the read failure retried (not given up after 1, 5 or 20 failures), decodes, allocation, eviction and keys; the size check; both warning texts; `Dispose`. All killed (a maintainer's script kept with the review notes, outside this repository; it rebuilds after its last restore).
- **Coverage** (all four suites, merged per file): PageRenderer, TextRenderer, RenderContext and Exporter 100 %, CliApp 99.2 %, WordArt 97.3 %, PictureCache 96.5 %; the uncovered lines are failure paths no test input reaches (a WordArt envelope that fails, the CLI's warning when its scratch folder cannot be deleted, a decoded copy whose pixels cannot be read back).

## Code review

Round 1, on a snapshot of this branch (`4c10232`): not clean (one Medium, three Low, seven nits). Rev 6 fixes each,
its test first; the blame says which part of the plan let it through.

| # | Finding | Blame | Fix |
|---|---|---|---|
| F1 | Seven shadow rules had no test that failed when they broke: the shadow's colour, the line's alpha, invisible shapes, WordArt's outline margin, the PDF's 300 dpi, the PDF mark's restore, z-order | TEST-GAP | AC2's new rows, the ½ pt AC1b row, the z-order and mark tests; each rule's mutation is in the battery |
| F2 | A picture's first verdict lasted for the context: a file locked once stayed a placeholder after it was free | AC-GAP | AC4 amended: a read failure is retried at the next lookup; a file read is not read again (said in the `RenderContext` summary and the README) |
| F3 | WI-004 said shadows now cost what their elements cost; in a PDF, a large translucent element's shadow is still an image of its size | PLAN-GAP | WI-004's sentence corrected; WI-006 F4 charges PDF shadow layers by their area |
| F4 | `image-too-large` said the file "was not opened"; it is read, only not decoded | HOUSE-RULE | the message, pinned by its test |
| N1 | The interface table named a test that does not exist | PLAN-GAP | the real name |
| N2 | "Embedded once per crop" is an upper bound | PLAN-GAP | "at most once per distinct crop" |
| N3 | Four AC3 mutations named here were not in the battery | PLAN-GAP | added |
| N4 | The slowest-file row mixed files and runs | PLAN-GAP | paired runs, each row one file |
| N5 | `DrawShadow`'s summary left out thin lines and page-sized layers | HOUSE-RULE | the summary |
| N6 | A decoded copy that could not be allocated threw mid-draw, where the comment said the encoded image is drawn | HOUSE-RULE | the allocation is tried, the encoded image drawn, nothing remembered; a test |
| N7 | The plans cited review files and a tool that are not in this repository | PLAN-GAP | said so, and the procedure described |

Round 2, on rev 6 (`499a29e`): not clean (four Low, two nits), all in what rev 6's tests pinned and its sentences
said; no defect in the code. Rev 7 makes each rule's tests cover the rule's whole range (every verdict, every shadow
route) rather than one case, and searched for other copies of each corrected sentence (round 3 found one the search
missed, R3-N2):

| # | Finding | Blame | Fix |
|---|---|---|---|
| R2-F1 | The retry test pinned one edge: retrying a read failure only once, or re-reading a missing, undecodable or over-limit file, went unseen | TEST-GAP | the retry over shadowed placements and two pages; a theory over every remembered verdict; five mutations |
| R2-F2 | The z-order test covered opaque shapes only | TEST-GAP | one page with every shadow route; a mutation per route |
| R2-F3 | Three comments (`Reads`, `DrawImage`, the `RenderContext` summary) said "read once" or "embeds it once" where a read failure is retried, a crop embeds again, and a missing file is remembered | HOUSE-RULE | each comment, and every other copy of those claims |
| R2-F4 | WI-004's sentence said a repeated picture is read, decoded and embedded once | PLAN-GAP | the sentence, this plan's title and AC4's headline |
| R2-N1 | `image-unreadable` said "could not be decoded" for a file that could not be read | AC-GAP | the message says which; AC4 amended; both texts pinned |
| R2-N2 | AC1's table row was broken over two lines | PLAN-GAP | one line |

Round 3, on rev 7 (`e9dbf7c`): not clean (three nits), no defect in the code. The second round in a row whose
findings came from the previous fixes, so rev 8 changes how its sentences are checked: the added prose and comments of
the documents and the production code (not yet the tests': R4-N2) were searched for absolute words (once, never,
every, only, unchanged, as before, cost what, and others), and each hit was checked against the code and the
measurements, not only the copies of the sentences a finding named. That search also found Task 6's "shadows no longer the cost", round 2's "checked every copy", "every `Exporter` method"
(`PixelSize` takes no context) and two comments saying WordArt is always drawn twice; all corrected here.

| # | Finding | Blame | Fix |
|---|---|---|---|
| R3-N1 | The `PictureCache` summary said each file is "looked up once", against its own next clause; the theory row said "looked up again" for "read again" | HOUSE-RULE | "read at its first lookup … read again at its next lookup"; "read again" |
| R3-N2 | The title still said "Shadows cost what their element costs" (F3's claim) | PLAN-GAP | the title says what AC1 says |
| R3-N3 | The mark test's throws did not prove they came from inside the exporter: with a check of every element before drawing, "restored on return only" would pass | TEST-GAP | each throw proves the exporter had begun (a decode; PDF bytes); two mutations with such a check |

The lock in the read-failure test is now held over ten pages (20 lookups), so a cache that stopped retrying within
20 failures fails it; the round-3 reviewer measured that cap and recorded it as an observation, not a finding.

Round 4, on rev 8 (`1c4e616`): not clean (four nits), no defect in the code; nothing drawn, written or warned
changes.

| # | Finding | Blame | Fix |
|---|---|---|---|
| R4-N1 | The `PictureCacheTests` header and a section label still said a picture is "decoded once" (a copy the cache dropped is decoded again; one over the budget never is); two comments said only Windows compares file names without case, which macOS's usual file system also does | HOUSE-RULE | the header and label name what the class tests; the comments say what the cache does |
| R4-N2 | Rev 8 said its search for absolute claims covered the whole change; it did not reach the tests' comments | PLAN-GAP | said as it was; rev 9 searched the tests' comments too, which found R4-N1's and one more ("an exclusive open blocks readers there only": .NET emulates such locks on Unix too) |
| R4-N3 | The PDF half of the mark test showed only that a page was begun, not that the mark was set: a check between a page's start and the mark, with the restore on return only, passed | TEST-GAP | each throw is checked to come from the drawing (`TargetSite` in `PageRenderer`); four mutations put a check before the mark |
| R4-N4 | The lock test's comment said wrongly where a cache that gives up would fail | HOUSE-RULE | the comment; a mutation that gives up after 20 failures |

Round 5, on rev 9 (`e8353a7`): not clean (one nit that blocks: a claim a later change would rely on; two that do
not), no defect in the code.

| # | Finding | Blame | Fix |
|---|---|---|---|
| R5-N1 | The mark test's comment said a check made before drawing would throw elsewhere; a check kept in `PageRenderer`, or a pre-pass that draws to a recorder, throws there too, so "restored on return only" passed | TEST-GAP | the test no longer asks where the throw came from: the drawing call that throws has first drawn something as only the exporter's mark draws it (a decode; a silhouette shadow, beside a picture SkPDF writes when drawn), so the mark was set when it threw; the reviewer's mutants added |
| R5-N2 | "65-byte" for a liar PNG of 57 bytes | HOUSE-RULE | 57 |
| R5-N3 | The Gate sentence named revisions 6 to 8 | PLAN-GAP | it names none |

## Not in scope

Render limits for hostile input (WI-006); rendering fidelity itself; a GUI.
