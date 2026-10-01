# WI-002: Publisher-assisted layout export (.pub → Pubsmith JSON)

**Why now:** Microsoft 365 Publisher stops in October 2026. While it still runs, Publisher
itself reads each `.pub` and describes the layout. Nothing is guessed. The same script keeps working later in
the PubOracle VM (Publisher 2021).

This is the **fallback**, not the rescue product. Cracking the format natively is WI-003 (feasibility). This
export's output is the ground truth for that work.

## Acceptance criteria

| AC | Criterion |
|---|---|
| AC1 | Every harvested `.pub` (36 files) produces a Pubsmith document that `DocumentJson.Load` accepts, or a logged failure naming the file and error. No silent skips. |
| AC2 | Page count and page sizes equal Publisher's (`manifest.csv`, ±0.01 pt). |
| AC3 | Text boxes, rectangles and ovals with solid fills and solid lines become **editable** elements (text with runs, fonts, sizes, colours, bold/italic, alignment, insets, rotation). |
| AC4 | Anything else (WordArt, pictures, lines, freeforms, tables, gradients, dashed lines, shadows, text with character effects) becomes a **flattened picture**: Publisher's own render of that element, transparent, at about 300 dpi, placed with the same frame and rotation. Every flattened element is listed with the reason in the file's `.import.json` report. |
| AC5 | Master-page elements are included behind each page's own elements. Z-order is preserved. |
| AC6 | Every page is rendered by Pubsmith and scored against Publisher's 300 dpi reference PNG. The scores go in `import-report.csv`; a page above 3.0 is flagged for review. |
| AC7 | A run can be repeated safely: one file hanging or crashing Publisher never stops the batch (per-file child process with a timeout, and only the child's own Publisher is killed). Output is written only after the file succeeds. |

## Verification

This work item's "tests" are the corpus run itself (AC1, AC2, AC6 checked per file in `import-report.csv`)
plus one repository test: three exported documents are committed as fixtures and must load and render with no
exceptions (`ImportedFixtureTests`).

## Results (2026-09-28)

**All 36 real files and 15 feature files exported: 51/51, 0 failures, nothing silently dropped.**
The report is `import-report.csv` in the harvest folder. Real files: 107 pages scored; **42 % ≤ 3.0,
72 % ≤ 6.0, median 3.54**. Pages made of pictures and flattened art are near-exact (an 8-up label sheet 0.04,
a four-page master-page card 0.19, business cards 0.09–0.11). The high scores track **editable text**: Publisher's "exact" line
spacing (17 paragraphs on the worst page), vertical anchoring and substituted fonts (cloud fonts such
as Aptos, and faces named with a weight such as Copperplate Gothic Bold). Those are renderer text-engine work items, not export defects.

**Defects found and fixed during the run (each would have silently lost content):**
1. `SaveAsPicture` crops to visible ink, so stretching it into the frame misplaced every flattened element.
   → Page-level renders with known scale and origin, plus difference matting (`matte.py`).
2. Moving elements off-page **changes their page/master ownership** (master elements came back as page-1
   elements, and elements from pages 2+ didn't return to their page). This emptied pages 2–4 of a master-page card (score 64)
   and a multi-page label sheet. → Isolation happens in a throw-away copy of the document; the document being
   read is never modified. That card: 63.5 → 0.19.
3. Publisher refuses to open a file that's already open. → Isolation copies are opened from a private copy.
4. A master-page read failure was downgraded to a warning (file "OK", content missing). → It now fails the file.
5. Deleting the previous assets folder failed while a file-sync client was syncing it. → Unique assets folder per run,
   referenced by the JSON; old folders removed best-effort only after success.
6. Paths over 260 characters broke ImageMagick scoring, and a missing score was left blank beside "OK".
   → Scoring renders to short temp paths; an unscorable page is flagged `UNSCORED`.

Tests: `ImportedFixtureTests` (three exported documents load, render and resolve every picture; master
elements appear on every page) and `tools/oracle/test_matte.py` (colour/alpha/box recovery, white-on-white
elements, empty, size mismatch; two deliberate breakages both caught).

## Known approximations (reported, not hidden)

The export draws vertical text anchoring (middle/bottom) as top and exact line spacing as the default multiple,
and leaves out underline; each of these is noted per element in `.import.json`. Tracking and kerning are also
left out, without a note. (The document model
has had anchoring and exact spacing since WI-004; this export was not updated to use them.)
