# WI-007: A PDF page's end, the caller's stream faults, and every render's memory are bounded (to be planned after WI-006)

**Goal:** what SkPDF holds for a PDF page, and writes at once when the page ends, is bounded; a fault in the output
stream ends a render with that fault instead of hanging it; warnings are bounded; and every accepted render's peak
memory is bounded and stated per class.

**Status:** not planned yet. WI-006 adds the drawing-time limit, the counted and per-element limits, the PDF and PNG
byte limits and the crash guards, which change every cost below. This file records what the plan must answer, from
WI-006's Task 0 reviews (rounds 1 to 6), so none of it is lost.

## Requirements carried from WI-006

- **A PDF page's end** (WI-006 Task 0 round 3 S3, round 4 U1 and U2, round 5 V1 to V3, V5 to V7; moved here by the
  owner after three rounds): SkPDF holds a page's whole content stream until `EndPage`, then compresses it and writes
  it at once. Measured, per second of drawing: up to about 230 MB of memory, 57 MB written and an end of 4.6 times
  the drawing time (80 WordArt of 1,024 glyphs: drawing 1.08 s, end 4.99 s, 61.4 MB). WI-006's rev 5 counted it per draw site
  (its Measurements hold the per-site data: a rectangle 19–31 B, an ellipse up to 7.9 KB, an oval-masked picture up to
  8 KB, a WordArt glyph 1.1–16.7 KB, culled text 0 B), and round 5 beat that count: SkPDF writes every number in full
  (up to 47 characters: a rectangle from 18.7 B to 166 B at ±1e38, an ellipse from 7.9 KB to 30.4 KB), a colour or
  alpha per instance (18.7 B to 66.6 B), Type3 fonts (25.5 B a glyph against 5.4 B), conic points (82 B a point
  against 31 B); it culls text by device bounds, not by the frame (a box rotated 45° held 217 times its count), and
  ink outside a line's box is held too (exact line spacing below the font's height: 939 KB, about 200 times its line
  boxes); and silhouettes drawn without a path were not in rev 5's probe (round 5 measured a filled ellipse with its silhouette shadow at
  15.9 KB, the silhouette alone about 8.0 KB). Any bound must also keep ordinary pages far below it: with worst-case
  weights, rev 4's count put WI-004's linked-story page at 25 MiB, though SkPDF holds 0.02 MB of it (it culls the
  story's lines outside the frame). Round 5's recommendations: a
  generated gate test (random pages with extreme parameters, asserting count ≥ held, reading only the page's
  `/Contents` stream, not ICC profiles or Type3 glyph procedures); culling taken from the canvas (`QuickReject` on a
  line's bounds widened by its fonts' extents); bounding or rounding the numbers the renderer writes; or a different
  mechanism altogether (round 3 offered a per-page share of drawing time as the cruder alternative). What SkPDF holds
  until the close belongs here too: about 230 B a page, 48 B a page for each image the page uses, 34–36 B for each
  distinct graphic state, about 55 B for each image or layer written (about 150 MB for a document at WI-006's load
  limit, extrapolated).
- **Fault containment** (round 1, F11, F22): WI-006 counts the PDF's bytes through a stream that forwards to the
  caller's; when the caller's stream throws while SkiaSharp writes a picture (a full disk, say), the exception crosses
  SkiaSharp and the process hangs (reproduced). The counting stream must catch the inner fault, stop forwarding and
  record it; the exporter then aborts and rethrows it. A hang guard for that case is the one wall-clock test.
- **Warnings** (round 1, F20): cap the list and the de-duplication set; say whether repeats count.
- **Memory per class** (round 1, F17; round 2, R9, R14): PDF layers (a 20,000 pt page with one layered shadow peaked
  at 4.6 GB before WI-006's layer guard), PNG layers (a second page bitmap), the `doc.json` load (2.3 GB at 256 MiB),
  typefaces (400 peaked at 3.9 GB or more), decodes repeated by eviction, SkPDF's buffered page content (above),
  WordArt's paths, encoded and decoded pictures, the encoded PNG held beside its bitmap, and text layout (1 Mi
  one-character runs peaked at 0.5–0.7 GB). A bound per class, checked, and a statement of the peak for one command.
