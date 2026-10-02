# WI-004: Native .pub reader, two-week spike (go/no-go per WI-003)

**Goal:** Pubsmith opens Publisher 2003–2021 `.pub` files by itself, with no Publisher and no
LibreOffice, and turns them into Pubsmith documents faithful enough to rescue people whose Publisher
has stopped working. Published for reputation, not revenue: correctness and honesty about losses matter more
than breadth.

**Gate (from WI-003):** GO if the native reader scores ≤ 3.0 on ≥ 80 % of real-file pages, with no silent
losses, by the end of the spike.

**Reference inputs**
- A harvest folder of the maintainers' own real publications (36 files, kept outside the repository): Publisher's
  own 300 dpi page renders (the scoring reference) and Publisher-assisted layout JSON for each (WI-002) as
  element-level ground truth.
- The diff corpus: 142 single-change variants built by Publisher itself (`tools/oracle/New-DiffCorpus.ps1`),
  each with Publisher's layout export as ground truth. This is the reverse-engineering corpus; it is committed
  under `tests/fixtures/pub/` and outlives Publisher's retirement.
- `docs/format/pub-format-notes.md` — the format as libmspub understands it, in our own words, plus our own findings.

## Acceptance criteria

| AC | Criterion |
|---|---|
| AC1 | Every one of the 36 real files opens without an exception. A file that can't be read raises `PubFormatException` naming the file and the reason (not a crash, not an empty document). **Amended 2026-09-30:** the same holds for damaged and hostile input: only `PubFormatException` (or a document with its losses reported) may come out, within bounded time and memory; and ordinary publications of realistic size still import (at least a 1,000-page book with a master, 300 pages of one linked story, 100 pages of 200 shapes). **Amended again (rounds 6 and 7):** bounded means at most 250,000 placement units; at most 8,388,608 characters of text placed (text runs, WordArt, picture paths, and font names beyond their first 32 characters, each time placed); and at most 256 MiB of document JSON, enforced while it is streamed (a larger document is refused before anything in the output folder changes, and the command names the file). The report holds at most one issue per unit, and neither JSON file is ever held as one string. A 1,000-page book of 6,000 characters a page, in 4 runs a paragraph, still imports. |
| AC2 | Page count and page size equal Publisher's for every real and diff-corpus file (±0.01 pt). |
| AC3 | Shape geometry: position, size, rotation and flips of every top-level shape in the geometry variants are within 0.1 pt / 0.1° of Publisher's ground truth; z-order matches. |
| AC4 | Fill and line: solid fill colour, fill transparency, line colour and line weight match ground truth for the fill/line variants; scheme colours resolve to the same RGB Publisher reports. |
| AC5 | Pictures: every picture shape gets its own image (full resolution, original encoding) with crop as source fractions; the oval mask and border are reproduced. |
| AC6 | Text: each text box's plain text equals ground truth (all real files). Character runs (font, size, bold, italic, colour) and paragraph alignment, line spacing (multiple **and** exact), and space before/after match for the text variants. |
| AC7 | WordArt becomes styled text: its text, font and fill match ground truth. |
| AC8 | **No silent losses.** Every shape the reader can't reproduce appears in the import report with a reason, and on the page as a visible placeholder. The page count of shapes read equals the count in the file. **Amended 2026-09-30:** anything that cannot be placed at all (a shape a page lists with no drawing, a damaged record, an unreadable text or picture layer) is reported as dropped; "the count in the file" is the shape references in the file's own page lists, masters included. Shapes marked deleted in the file (MS-ODRAW fDeleted) are neither drawn nor counted. A reference to a shape with no drawing is reported once per page list that names it (a master's list is not reported again for every page). |
| AC9 | **Gate:** fidelity ≤ 3.0 on ≥ 80 % of real pages (same metric as WI-001/2). |

## Test plan (tests before code; red first)

Two tiers:
- **Committed fixtures (always run):** the 142 diff-corpus `.pub` files plus Publisher's ground-truth JSON and
  a few of its 300 dpi renders, in `tests/fixtures/pub/`.
- **Corpus suite (opt-in):** runs over a harvest folder when `PUBSMITH_CORPUS` points at it. It produces per-file
  results plus the AC9 gate report. It's opt-in because real files live outside the repository; it must be run
  for any gate claim.

Test names corrected on 2026-09-30 to the tests as written (the first draft used working names).

| AC | Tests (class.method) | Tier |
|---|---|---|
| AC1 | `CorpusTests.EveryRealFile_OpensWithPublishersPageCountAndSize` · `PackageAndOfficeArtTests.NotAPubFile_ThrowsPubFormatException` / `Truncated_ThrowsPubFormatException` / `MissingFile_ThrowsPubFormatException` · `HostileInputTests` (crafted overflow, recursion, bomb, table and budget cases, and `CorruptedFiles_OnlyEverFailAsFormatErrors`: 7,000 corrupted variants of Publisher-built files) · `AggregateLimitsTests` and `AggregateLimitsRound3Tests` (aliasing, repetition, damage granularity, fixture metadata) · `PublicationScaleTests` (ordinary large publications import; hostile repetition stays bounded) · `WorkPerUnitTests` (work done on every placement is charged; strings decoded once and capped; output proportional to the units charged) · `OutputCeilingTests` (the text budget, from bytes and per kind of string; `AHostileDocumentWithinBothBudgets_IsRefusedAtTheJsonLimit`; `WriteTo_ARefusedDocument_ChangesNothingInTheFolder`; `WriteTo_ThatFailsToMoveItsDocument_RemovesNoEarlierPicture`; `WordArtInAnOrdinaryFont_CostsOnlyItsText`; `ALongNovel_Imports_ItsTextIsCountedExactly_AndItsJsonIsFarBelowTheLimit`; `ParseTimeIssues_CountAgainstTheUnits`; `WriteTo_StreamsItsJson`) · Core `DocumentJsonTests.Save_StreamsTheDocument` / `Save_WritesExactlyWhatSerializeReturns` / `Save_LargerThanItsLimit_IsRefused_AndKeepsTheEarlierDocument` / `Save_AtItsLimit_IsSaved` / `Save_ThatFailsPartWay_KeepsTheEarlierDocument` / `Save_WithANegativeLimit_Throws` / `Stage_WritesBesideThePath_AndOnlyCommitReplacesIt` / `Stage_DisposedUncommitted_LeavesThePathAsItWas` / `Stage_ACommitWhoseMoveFails_KeepsTheEarlierDocument` / `LimitedStream_IsWriteOnly_AndRefusesAWritePastItsLimit` · CLI `HostileFileTests` (a 100 KB hostile `.pub` on disk: `import` and `render` refuse it by name and write no file) | corpus / fixtures |
| AC2 | `TruthTests.EditableElements_MatchPublisher` (page count, every variant) · `ContentsTests.PageSize_IsReadInEmu` · `CorpusTests.EveryRealFile_OpensWithPublishersPageCountAndSize` | fixtures / corpus |
| AC3 | `TruthTests.EditableElements_MatchPublisher` (0.1 pt, 0.1°) · `ImporterTests.Rotation30_KeepsTheFrame` / `Rotation90_UnswapsTheStoredBox` / `ZOrder_FollowsTheDrawingStream` · `MappingRulesTests.Flip_IsReported` / `RotatedGroup_TurnsClockwise` / `RotatedGroup_FillsTheFramePublisherExported` / `ChildBox_ScalesEachAxisOnItsOwn` · `NativeFidelityTests.RotatedGroup_MatchesPublisher` | fixtures |
| AC4 | `ImporterTests.Rectangle_PositionSizeFill` / `LineWeightAndColour` / `Transparency_BecomesAlpha` · `MappingRulesTests.FillOpacity_BecomesAlpha` / `LineVisible_FallsBackToTheTertiaryBorderFlags` / `ColourFlags_OtherThanRgb_AreApproximate` · `QuillTests.ColorResolver_ReadsLiteralAndPaletteReferences` | fixtures |
| AC5 | `ImporterTests.Picture_HasItsOwnImageAndFrame` / `PictureCrop_IsASourceFraction` / `PictureBorder_BecomesStroke` / `OvalMask_IsApplied` · `MappingRulesTests.LargeCrop_IsKept` / `ImpossibleCrop_IsClampedAndReported` | fixtures |
| AC6 | `QuillTests` (text, bold, italic, size, colour run, font face, alignment, exact and multiple spacing, space before/after, empty paragraphs) · `ImporterTests.TextBox_TextFontInsets` / `ExactLineSpacing_IsCarried` · `MappingRulesTests.VerticalAnchorBottom_IsCarried` · `CorpusTests.EveryTextBox_TextMatchesPublishersExport` | fixtures / corpus |
| AC7 | `ImporterTests.WordArt_BecomesStretchedStyledText` / `WordArtOutline_BecomesTheTextOutline` / `WordArtWarp_IsMapped` · `MappingRulesTests.WordArtBoldAndItalic_ComeFromTheGeometryTextFlags` · `NativeFidelityTests` (pixel comparison with Publisher's renders) | fixtures |
| AC8 | `ImporterTests.Unsupported_IsPlaceheldAndReported` / `Approximations_AreReported` / `ShapesAccountedFor_EqualTheFilesPageLists` · `HostileInputTests.ShapeListedOnPage_WithoutDrawing_IsReported` / `RealFixtures_ReportNoDamage` · `MappingRulesTests.ShapeWithoutPosition_IsReportedAsDropped` / `EmptyGroup_IsReportedAsDropped` · `PublicationScaleTests.AMasterNamingMissingShapes_IsReportedOncePerShape_NotPerPage` · `WorkPerUnitTests.TwoPageListsNamingTheSameMissingShape_ReportItTwice` · `OutputCeilingTests.QuillFontNames_AreCutAndReported_AndTheNextFontIsRead` / `ManyCutFontNames_AreReportedOnce` / `ACutFontName_IsReportedThroughImport_AsApproximated` / `RunFontNamesLongerThanTheCap_AreCutAndReported` / `DuplicateTextIds_AreReportedOnce` | fixtures |
| AC9 | `CorpusTests.Gate_FidelityReport` (writes `native-report.csv`; asserts only that every file imports, since the gate is a decision) | corpus |

Every interface member of `Pubsmith.PubReader` gets a direct test, and the 85 % coverage floor applies to its
files. Key tests are mutation-checked: for the remediation layer of 2026-09-30, one mutation per guard or rule,
each of which a named test must fail (see Results).

## Design

- Project `src/Pubsmith.PubReader` (depends on Core). Compound-file access via **OpenMcdf** (MPL-2.0,
  NuGet). Layers: `PubPackage` (streams) → `OfficeArt` (records, shapes, properties, picture store; public spec
  MS-ODRAW) → `Contents` (pages, masters, shape ↔ page) → `Quill` (text and formatting) → `PubImporter`
  (maps to `PubsmithDocument` and lists every loss as an `ImportIssue`).
- Units: EMU (12,700 per pt), relative to the page centre (confirmed by differential test).
- What the model can't express yet is never dropped silently. WordArt warps, shadows and rotated groups are
  drawn; gradients (as their first colour) and dashed lines (as solid) are drawn approximately and reported;
  freeforms, other preset shapes and tables are reported placeholders.
- Hostile input: every length and offset is checked in 64-bit arithmetic. Limits hold in aggregate, not only
  per item: container nesting; stream size (never more than the file holds); chunk count; run tables (never more
  runs than the stream has room for); formatting records (parsed and summarised once each, within a parse budget);
  pictures (each record read once; everything inflated or copied, including attempts given up part-way, within
  twice the file's drawing streams plus 64 MB); and the work of placing things (pages, backgrounds, every shape a
  page lists, every shape converted including group members and shapes that draw nothing, every issue reported,
  and the paragraphs, runs and characters of text placed, each time it is placed), and separately the strings
  placed (text, WordArt, picture paths, font names beyond 32 characters: at most 8,388,608 characters) and the
  document JSON written (at most 256 MiB, enforced while it is written). A story shared by linked text
  boxes is placed once, in the first box that shows it (text flow between boxes is not reproduced yet). A damaged shape or group costs that record, damaged text or pictures cost that
  layer (all reported), not the file; anything unexpected is wrapped in `PubFormatException` naming the file.
  Out-of-memory is deliberately not caught; these limits are meant to keep a small file far from it. The JSON
  document and report are written as streams, never held as one string. Rendering is not budgeted yet (WI-006).
  Since WI-005 a repeated picture is read once, decoded once while it fits the cache budget, and embedded at most
  once per distinct crop, and a shadow costs about what its element costs in PNG, and in PDF for opaque shapes; any other shadow in a PDF is still an image the size of its element
  (about 6 ms for a page-sized one). So hostile shapes (very long WordArt, very many pages, heavy overdraw, many
  large translucent shadows in a PDF) can still take minutes to render.
- CLI: `pubsmith import <file.pub> [--out <dir>]` writes the document JSON, its pictures and the report;
  `pubsmith render <file.pub> --pdf/--png` converts directly.

## Results

### Day 1 checkpoint (2026-09-28)

| Measure | Result |
|---|---|
| Real files opened (AC1) | 35/36; the one older-format file is reported as unsupported |
| Page count + size vs Publisher (AC2) | 35/35 supported files |
| Diff-corpus variants matching Publisher's layout export (AC3/4/6) | **142/142** (in 41 of them Publisher's export turns every element into a picture, so those compare page count and size only) |
| Real text boxes whose text matches Publisher's export (AC6) | 429/432 (misses: linked text boxes in one brochure, one business card) |
| Shape accounting (AC8) | read = converted + placeheld, for every file |
| **Gate (AC9): real pages ≤ 3.0** | **18 % (19/106)**; 35 % ≤ 6.0; median 8.80. Target 80 %. |

Where the gate is lost, in rough order of pixel impact (from side-by-sides):
1. WordArt warps (arches, circles) drawn straight; shadows (shapes, WordArt, picture rings) not drawn.
2. Picture adjustments: brightness/contrast (washed-out backgrounds), recolour.
3. Gradient and picture/texture fills drawn as a solid colour.
4. Custom geometry (freeforms, e.g. a mountain drawing in one real file) and non-rectangle presets are placeholders.
5. Tables are placeholders (calendars: 11 per file).
6. Text details: indents, underline, tabs, linked text boxes, cloud fonts (e.g. Aptos) substituted.

### Review remediation (2026-09-30)

Two independent adversarial reviews (robustness; correctness, tests and prose) led to one remediation layer:

- **Hostile input (AC1 amended).** Nested containers, integer overflow in bounds checks, a decompression bomb,
  unbounded Quill tables and quadratic lookups could crash the process or run without bound. Each is now bounded
  and has a crafted-input test; a seeded fuzz test imports 7,000 corrupted variants of Publisher-built files
  and accepts only a document or a `PubFormatException` raised on purpose, each within 3 seconds.
- **Losses reported (AC8 amended):** shapes a page lists with no drawing, damaged drawing records, unreadable
  text or picture layers, stories without ids, duplicate ids and truncated property data are reported; the
  document keeps whatever could be read. Measured on every fixture: shape references in the page lists =
  shapes read + references reported as dropped.
- **Checked against Publisher and fixed:** rotated and flipped groups now turn their members (real files: pages
  19.37 → 15.75, 20.28 → 15.93 and 2.54 → 0.68); empty paragraphs are as tall as their own formatting
  (Publisher's stanza gap is 2.0 line pitches; the old default gave 1.53); WordArt bold (the flag matches the
  files the feature corpus built with bold), italic, transparency and unfilled WordArt; large crops kept.
- **Checked and not changed:** bottom-anchored text with space after its last paragraph. Publisher does not store
  space after on a text box's last paragraph, and our render matches Publisher's to 1 px with and without it.
- **Tighter proof:** the truth comparison now uses AC3's own 0.1 pt / 0.1° (it had used 0.6 pt / 0.2°); the
  largest measured difference over the 111 compared elements is below 0.005 pt.
- **Gate (AC9):** unchanged in aggregate, **18 % (19/106) ≤ 3.0**, median 8.63; 25 pages better, 9 worse. The
  worst regression, one poem page (11.53 → 15.21), comes from a separate existing error that correct blank-line
  heights now expose: our line pitch for that face is about 5 % taller than Publisher's. That is the next
  text-layout item.
- **Mutation check:** 83 mutations, one per guard or rule in the layer (each reverting a single fix), all fail
  their named test.

Two further review rounds found limits that held per item but not in aggregate (aliased pictures, aliased run
tables, formatting records re-scanned per run, group members and shapes that draw nothing escaping the placement
cap, long text repeated on many pages), an unpinned rotation direction, damage that cost more than the damaged
record, and publication issues (a test picture that needed replacing, tools that could stop a Publisher the user
had open). The code issues are fixed, each with a test that a mutation of the fix fails; the picture and the
tools were fixed and checked by hand against Publisher (the tools now refuse to start while Publisher is open).
The rotated group also matches Publisher's own render (score 0.05; turned the other way, 18.13). Real files are
unaffected by these later rounds: the remediation's corpus results above are unchanged. (The placement units the
real files use are measured after the fifth round, below.)

A fourth round found that charging text every time it is placed also charged long linked stories once per box
(a 120-page linked book was rejected), and that issues for missing shapes could multiply per page without a
charge. The cost model was redesigned rather than patched again: every issue and every page-list look-up costs a
unit, missing shapes are reported once per page list, and a linked story is placed once. Tests now pin both sides
of the budget: ordinary large publications must import, hostile repetition must stay bounded. On the real files
only the linked brochure changed (page 2: 14.56 to 14.03).

A fifth round found work done on every placement that was still free (deleted shapes, including deleted group
members; repeated entries in a page's shape list; many deleted shapes sharing one id), WordArt strings decoded
again on every placement, and font names of any length. Each now costs a unit or is decoded once, and font names
are cut at 256 characters (real names are at most 31) with an issue. A test also checks that the output stays
proportional to the units charged (at most 8 KB of JSON per unit). The real files are unchanged by this round (same
scores, placeholders and issues for every file); the largest of the 192 harvested files (36 real publications plus
the test files) uses 2,412 of the 250,000 placement units, and the median file 5.

A sixth round found that the units bound how many things are placed but not what they carry: one unit of text
could carry a kilobyte of characters, each written as six (`\uXXXX`), so a 446 KB file imported within the units
and then needed 3.5 GB to write about 1.4 billion characters of JSON. The strings placed (text, WordArt, font
names, picture paths) now have their own budget of 8,388,608 characters, and the document and report are written
as streams. (This round also stated an output ceiling per unit; the seventh round found it false, below.) The
round also found that a font name cut by the text reader was not reported (it now is, once per font), and that the
VM guide left out the helper script the export loads. The real files are unchanged by this round; the largest
places 34,624 characters of text (the median 28).

A seventh round found that ceiling false: a text box drawn with a frame, inside a group, writes two elements for
one unit, up to about 1,450 characters of JSON per unit. Rather than state a ceiling for every shape, the document's
JSON is now limited directly: 256 MiB, enforced while it is streamed; a document that would be larger is refused,
and the earlier document in the folder stays (the 1,000-page book above writes 74 MB). The round also found that problems
found while parsing could multiply (a file reusing one text id a million times gave a million issues): each kind is
now reported once, and they count against the units. Font names now cost text only beyond their first 32
characters, so a book in ordinary fonts pays only for its text. A cut font name is reported as approximated, not
dropped. The real files are unchanged by this round; with font names free up to 32 characters, the largest now
places 25,632 characters of text (the median 24).

An eighth round found that a refused re-import still replaced the earlier document's pictures, which were written
before the document reached the limit. The document is now written first, beside its name, and moved into place
last, so a refused document writes no file and changes nothing already in the output folder; `pubsmith import` and
`render` name the file when they refuse one. Two small guards gained tests, and later rounds added tests for a
re-import that fails writing its report or moving its document into place: the earlier document stays, no earlier
picture is removed, and the new document is removed.

Tests after all rounds: Core 30, CLI 32, Rendering 127, PubReader 395 (+3 opt-in corpus tests). Real text-box
matches stay at 429/432; the opt-in test now fails if that number drops.
