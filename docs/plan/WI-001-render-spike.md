# WI-001: Rendering spike

**Goal:** prove that one engine (SkiaSharp) can draw a page from our own document format to screen/PNG
and PDF, and match Publisher's output closely enough to replace it. Measure it against Publisher's
reference renders captured on 2026-09-27 (the harvest's test-corpus renders; the three used here are in
`tests/fixtures/golden/`).

**Out of scope for WI-001:** the editor UI (Avalonia), rich text beyond single-style runs per paragraph,
gradients, shadows, WordArt, text wrap, tables, master pages, CMYK, and `.pub` import. Each has its own
feature file in the test corpus (F-numbers, `tools/oracle/New-FeatureCorpus.ps1`) and gets its own work item.

Product name: **Pubsmith** (`Pubsmith.*`; renamed from the working name on 2026-09-30).

## 1. Acceptance criteria

| AC | Criterion |
|---|---|
| AC1 | A document (pages with explicit size in points; shape, image and text elements with bounds and rotation) survives a JSON save/load with no loss. A document with an unsupported `schemaVersion` is rejected with an error naming the version. |
| AC2 | PDF export: every page's MediaBox equals the model page size (±0.01 pt). Fonts used on the page are embedded. |
| AC3 | PNG export at *d* dpi produces exactly `round(w/72·d) × round(h/72·d)` pixels. |
| AC4 | **Fidelity.** Test-corpus files F01, F04 and F12, rebuilt by hand in the model and rendered at 300 dpi, score at or below the threshold against Publisher's reference PNG, using the ImageMagick metric of the LibreOffice baseline (800×800 grey, blur 1.5, RMSE %): **F01 ≤ 2.0, F04 ≤ 2.0, F12 ≤ 1.3** (LibreOffice scored 4.1 / 4.5 / 1.3). Each file also has a deliberately wrong model that must score *above* its threshold, which proves the test can fail. |
| AC5 | The image comparer reproduces the ImageMagick metric: identical images score 0, and the stored LibreOffice-vs-Publisher F01 pair scores 4.1 ± 0.5. |
| AC6 | A missing font is **reported**: the warning names the requested family and the substitute used. It is never silent. A missing image asset is reported and drawn as a visible placeholder, and rendering doesn't fail. |
| AC7 | Image crop is stored as fractions of the *source image* (0–1 per side). Rendering shows only the cropped region, filling the frame. (Publisher's `CropLeft` is in points of the unscaled picture, which is why the model uses fractions.) |
| AC8 | Rotation is about the element's centre, in degrees, clockwise-positive (Publisher's convention). |
| AC9 | CLI: `pubsmith render <doc.json> [--pdf <path>] [--png <path>] [--dpi <n>]` exits 0 on success, 2 on a usage error, and 1 on an unreadable or invalid document. Warnings go to stderr. **Amended 2026-09-30:** any failure while reading or rendering (including a page too large to rasterise) exits 1 with a message, never a crash; `render` also takes a `.pub`, and `pubsmith import <file.pub> [--out <dir>]` writes the document (WI-004). |

## 2. Test plan (written before implementation; all tests start red)

### AC → test matrix

Test names corrected on 2026-09-30 to the tests as written (the first draft used working names).

| AC | Test (class.method) | Project |
|---|---|---|
| AC1 | `DocumentJsonTests.RoundTrip_AllElementKinds_IsLossless` | Core.Tests |
| AC1 | `DocumentJsonTests.Load_UnsupportedSchemaVersion_ThrowsNamingVersion` | Core.Tests |
| AC1 | `DocumentJsonTests.Color_SerializesAsHex_AndParsesWithAndWithoutAlpha` | Core.Tests |
| AC2 | `ExportTests.MediaBox_EqualsPageSize` (two pages of different sizes) | Rendering.Tests |
| AC2 | `ExportTests.TextPage_EmbedsFontFile` | Rendering.Tests |
| AC3 | `ExportTests.PixelSize_MatchesDpi` (Theory: 72, 150, 300 dpi) | Rendering.Tests |
| AC4 | `FidelityTests.F01_PageCustomSize_WithinThreshold` / `F01_Mutated_ExceedsThreshold` | Rendering.Tests |
| AC4 | `FidelityTests.F04_ImagesCropAlpha_WithinThreshold` / `F04_Mutated_ExceedsThreshold` | Rendering.Tests |
| AC4 | `FidelityTests.F12_Bleed_WithinThreshold` / `F12_Mutated_ExceedsThreshold` | Rendering.Tests |
| AC5 | `ImageComparerTests.Identical_ScoresZero` | Rendering.Tests |
| AC5 | `ImageComparerTests.LibreOfficeF01Pair_MatchesImageMagickScore` | Rendering.Tests |
| AC5 | `ImageComparerTests.DifferentSizes_AreNormalised` | Rendering.Tests |
| AC6 | `FontAndWarningTests.MissingFamily_ReportsSubstitute` / `InstalledFamily_ResolvesWithoutSubstitution` | Rendering.Tests |
| AC6 | `FontAndWarningTests.MissingFont_WarningNamesFamilyAndSubstitute_Once` | Rendering.Tests |
| AC6 | `FontAndWarningTests.MissingImage_DrawsPlaceholderAndWarns` | Rendering.Tests |
| AC7 | `GeometryTests.CropFractions_ShowOnlyCroppedRegion` (quadrant-coloured source) | Rendering.Tests |
| AC7 | `DocumentJsonTests.Crop_OutOfRange_IsRejected` | Core.Tests |
| AC8 | `GeometryTests.Rotate90_TallBarBecomesWide_AboutCentre` | Rendering.Tests |
| AC8 | `GeometryTests.Rotate15_IsClockwise` | Rendering.Tests |
| AC9 | `CliTests.Render_WritesPdfAndPng_ExitZero` | Cli.Tests |
| AC9 | `CliTests.MissingArgsOrUnknownCommand_ExitTwo` / `BadOption_ExitTwo` / `InvalidJson_ExitOne` / `MissingFile_ExitOne` | Cli.Tests |
| AC9 | `CliTests.Warnings_GoToStderr_ExitStillZero` | Cli.Tests |
| AC9 (amended) | `CliTests.RenderFailure_ExitsOne_AndLeavesNoFile` / `RunWithoutStyle_IsAnInvalidDocument_ExitOne` / `Import_WritesDocumentPicturesAndReport` / `NotAPub_ExitOne`; `RenderScratchTests.RenderPub_Directly_WritesPdfAndPng_AndLeavesNoTemporaryFile` | Cli.Tests |

### Interface coverage (public members → tests)

| Member | Tests |
|---|---|
| `DocumentJson.Serialize / Deserialize / Load / Save` | DocumentJsonTests (all) |
| `Rgba.Parse / ToString` | `Color_SerializesAsHex_...` |
| `PageRenderer.Render(SKCanvas, Page, RenderContext)` | FidelityTests, GeometryTests, FontAndWarningTests |
| `Exporter.ToPng(Page, dpi, ctx, stream)` / `RenderBitmap` / `PixelSize` | ExportTests, FidelityTests, LimitsTests |
| `Exporter.ToPdf(PubsmithDocument, ctx, stream)` | ExportTests, LimitsTests, CliTests |
| `FontResolver.Resolve(family, bold, italic)` / `SplitStyle` | FontAndWarningTests |
| `ImageComparer.Score(SKBitmap, SKBitmap)` / `Score(path, path)` | ImageComparerTests, Fidelity |
| `RenderContext.Warnings` / `BaseDirectory` | FontAndWarningTests, LimitsTests, CliTests |
| `RenderContext.Dispose` / `MaxPicturePixels` / `PictureCacheBudget`, `RenderWarning.ImageTooLarge` (WI-005) | RenderContextTests, PictureCacheTests |
| `CliApp.Run(args, stdout, stderr)` | CliTests |

Coverage floor: ≥ 85 % line coverage per touched file (coverlet `XPlat Code Coverage`, cobertura).

### Deliberate breakage each key test must catch

| Test | Breakage it must catch |
|---|---|
| F01/F04/F12 `_WithinThreshold` | Drop an element, or move one by 0.25 in: the score rises above the threshold. The `_Mutated_` twins do this on every run. |
| `MediaBox_EqualsPageSize` | Swap width and height in the exporter, or apply a dpi factor to the PDF page size. |
| `CropFractions_ShowOnlyCroppedRegion` | Treat crop as points, or ignore crop: the wrong quadrant colour shows at the probe pixels. |
| `Rotate15_IsClockwise` | Negate the angle. |
| `MissingFamily_ReportsSubstitute` | Return the fallback silently. |
| `LibreOfficeF01Pair_MatchesImageMagickScore` | Skip the grayscale or blur step: the score moves outside ±0.5. |

No opt-in suites. Every test runs in `dotnet test`. Fixtures are copied into `tests/fixtures/` so the
tests don't depend on files outside the repository.

## 3. Design decisions

- **Units:** points (1/72 in), stored as `double`. The page origin is the top-left corner, with y pointing down, like Publisher.
- **One drawing path:** `PageRenderer` draws to an `SKCanvas`. PNG uses a raster canvas, and PDF uses
  `SKDocument.CreatePdf`. Screen and print can't drift apart because they share the code.
  Since WI-005 the exporters tell the renderer which they are drawing: a bitmap draws pictures from a decoded
  copy, a PDF embeds each picture at most once per distinct crop, and a shadow's line width is judged at 300 dpi for a PDF; otherwise the
  drawing is the same code.
- **Format:** JSON (System.Text.Json), `schemaVersion: 1`. Elements use a type discriminator.
  Colours are `#RRGGBB[AA]` strings. Image sources are paths relative to the document file.
- **Fonts:** resolved via `SKFontManager`. A substitute always produces a warning (the cloud-font risk).
- **Text (spike level):** paragraphs of single-style runs, greedy word wrap inside the frame's inner margins
  (Publisher's default is 2.88 pt), and alignment left/center/right/justify. Line height is the font
  spacing × the paragraph's multiple (Publisher's default is 1.19), plus space-after (default 6 pt).
- **No database.** FunkyORM becomes the data layer when a template/asset library is added.

## 4. Tasks

0. **Test-plan review gate:** this document.
1. Scaffold the solution (Core, Rendering, Cli, and three test projects, with coverlet), plus fixtures. Every test is red.
2. Core model + JSON → turns the AC1 and AC7 (JSON) tests green.
3. ImageComparer → AC5 green.
4. Renderer (shapes, images with crop, text, rotation, font resolver, warnings) → AC6, AC7 and AC8 green.
5. Exporters → AC2 and AC3 green.
6. Hand-built F01, F04 and F12 models → AC4 green. Record the scores in §5.
7. CLI → AC9 green.
8. Coverage run and handoff.

## 5. Results (2026-09-27)

**Fidelity vs Publisher** (same metric as the LibreOffice baseline; lower is closer):

| File | Pubsmith | LibreOffice | Threshold | Wrong model (must be above) |
|---|---|---|---|---|
| F01 page size + text | **1.82** | 4.1 | 2.0 | 11.42 (inset frame dropped) |
| F04 images, crop, alpha | **0.22** | 4.5 | 2.0 | 10.10 (background dropped) |
| F12 bleed | **1.01** | 1.3 | 1.3 | 9.10 (image moved 0.25 in) |

**Tests:** 84 passing (Core 15, Rendering 51, CLI 18). **Coverage** (coverlet, merged per file): every file is
≥ 88.9 % (CliApp 93.7, DocumentJson 97.4, Exporter 88.9, PageRenderer 94.3, Rgba 94.7; the rest are 100).

**Deliberate breakage, run and confirmed caught (13/13):** rotation negated; crop ignored; crop read as
points; font substitution made silent; PDF width/height swapped; comparer without blur; comparer without
Rec.709 grayscale; shape strokes not drawn; justify never stretches; centring off by half; forced breaks
treated as spaces; text not clipped. The grayscale breakage *initially survived*: the LibreOffice pair is
almost pure black and white. `ColourWeighting_UsesRec709Luma` was added, and it now fails as it should.
Blame: TEST-GAP.

**Process note:** the text-layout tests (`TextLayoutTests`) and the entry-point test were written after the
code, to close coverage gaps, so they weren't red first. Each was then checked with a deliberate breakage
instead (justify, centre, forced break, clipping).

**UNVERIFIED / known limits**
- F01 has the tightest margin (1.82 against 2.0). Its remaining difference is mostly text position: the
  line-spacing model is an approximation.
- PDF font embedding is asserted by the presence of `/FontFile*`. The PDFs haven't been checked in Acrobat
  or preflighted for print.
- Fonts are resolved from installed Windows fonts only. Microsoft 365 cloud fonts (e.g. Aptos) will
  substitute and warn until licensed copies are installed.
- No independent adversarial review has been run on this work yet.
