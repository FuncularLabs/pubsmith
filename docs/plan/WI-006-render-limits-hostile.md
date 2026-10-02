# WI-006: Render limits for hostile documents (to be planned after WI-005)

**Goal:** a document that stays inside every import limit (or a hand-written `doc.json`, which has none) renders
within a stated time and memory, or is refused quickly by name; ordinary publications, including WI-004's large
ones, never meet a limit.

**Status:** not planned yet. WI-005 first removes the measured hot spots (shadows, repeated pictures), which changes
every cost below. This file records what the plan must answer, from the WI-005 plan and code reviews (maintainers'
notes kept outside this repository), so none of it is lost.

## Requirements carried from the review

- **F1** Limits injectable (a `RenderLimits` record carried by `RenderContext`, with public defaults); boundary tests
  at small limits; one test pins the defaults; deterministic observers instead of wall-clock thresholds.
- **F2** A numeric worst case inside all limits (time per format, peak memory), proved per cost class, including the
  WI-004 round-7 builder (`HostileFileTests`).
- **F3** Page count and PNG output (bytes and pixels) bounded per command.
- **F4** Drawn area bounded per command, not only per page; per-page cap re-calibrated (real maximum 4.5×). In a PDF,
  every layered shadow (anything but an opaque shape) is still an image the size of its element, about 6 ms for a
  page-sized one (WI-005 code review, F3): charge PDF shadow layers by their area.
- **F5** Every stretched (WordArt) glyph charged up front, before glyph paths are built; outline area charged.
- **F6** Runs and paragraphs charged (not only characters); glyph area clipped to the frame; layout stops below the
  frame; warnings capped; long font names charged.
- **F7** Picture decoding budget that cannot refuse ordinary documents (decode once per distinct picture, or decode at
  the drawn size); realistic ordinary tests (14.4 MP pictures, 100-page catalogues of distinct photos).
- **F8** PDF embeds charged. (WI-005 adds a header check for both outputs, at 250 megapixels. Below it, SkPDF still
  decodes every non-JPEG picture at full size even unshadowed, and a JPEG when shadowed or EXIF-turned: a 66-byte PNG
  declaring 225 megapixels costs 1.75 GB, and a failed native allocation there aborts the process (WI-005 rev-3 R1,
  rev-4 S7). WI-006 must bound that, e.g. a lower limit for what SkPDF will decode, or decoding at the drawn size.)
- **F9** Ordinary side: WI-004's 1,000-page book with a master, 300 pages of one linked story, 100 pages of 200
  shapes; body text; 4.5× overdraw.
- **F10** The CLI stages every output (PDF and every PNG page) and commits only when the whole command succeeds;
  earlier outputs unchanged on refusal.
- **F11** The PDF size limit through a counting stream that never throws inside SkiaSharp (a throw during a picture
  write hung the process); a managed check after each element, `EndPage` and `Close`; `Abort()` on any exception.
- **F13** Drawn extents in device space, clipped to the device clip, finite (NaN and infinity cannot disable or
  falsely trip a limit); a rotated shape charged by its area, not its bounding box.
- **F16, F18** Interface-coverage table, task → test map, documents to update.

From the rev-2 review of WI-005 (N12), also:
- A paragraph line spacing of 0 or less (allowed by doc.json) stacks every line on one baseline: bound or refuse it.
- `FontResolver`'s cache of resolved families grows with every distinct family name: bound it.
- F11's per-page memory: vector content is buffered until `EndPage`, so state and test the per-page bound.
- A watchdog test for the PDF limit crossed during a picture write (the hang F11 found).
- Library note: after a refusal, the output stream's contents are undefined (the CLI writes atomically).

From the rev-3 review of WI-005 (R10), also:
- **F12's charge half:** WordArt charged by its glyph bounds (path warps draw mostly outside the frame); envelope
  `Adjust` has no range check.
- **F17:** say whether the counters stay exhausted after a refusal (sticky).
- **Memory carried from WI-005:** WordArt holds one element's final paths (about 2.2× per glyph); encoded picture bytes
  are held for the context's life; a decoded copy peaks at 2× its size while it is made.
