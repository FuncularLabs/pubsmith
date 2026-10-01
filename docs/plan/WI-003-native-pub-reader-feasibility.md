# WI-003: Native .pub reader, feasibility and go/no-go (the "rescue" question)

**Question:** can Pubsmith open `.pub` files by itself (no Publisher and no LibreOffice), faithfully
enough to be a rescue option for people stranded by Publisher's retirement? If not, we stop chasing
Publisher compatibility and compete on user-facing features instead.

## What we know (evidence gathered 2026-09-28)

| Finding | Evidence | Implication |
|---|---|---|
| **One format family covers almost everything** | 35 of 36 real files have `Contents` magic `E8 AC 2C 00` (Publisher 2003–2021). One file is `E8 AC 22 00` (Publisher 98/2000 era). | One reader covers about 18 years of files. The older format is a long tail. |
| **Container is documented** | OLE compound file (MS-CFB). Read with `olefile` since day 1. | Solved. |
| **Pictures are trivial** | Every embedded image was extracted at full resolution (PNG/JPEG BLIPs in `Escher/EscherDelayStm`). Shape → image link is `pib` in each shape's properties. | Solved. |
| **Shape styling is in a documented format** | `Escher/EscherStm` holds MS-ODRAW (OfficeArt) shape containers. On a real label they gave: WordArt text, font, warp preset (spt 144/175), fill, outline, shadow colour/offset, rotation (−90°), picture index, text-story id. | Fills, lines, shadows, WordArt, geometry and rotation have a **public spec** (MS-ODRAW). |
| **Text content** | Story text and the font table come out of `Quill/QuillSub/CONTENTS` (UTF-16). | Text is easy. Per-character formatting needs reverse engineering. |
| **Shape position is decoded** | Differential test (one rectangle; saved at x=72 vs 73, y=72 vs 74, width 144 vs 150; plus rotation and colour changes). Every change landed in `Escher/EscherStm`. Position is record `0xF010` (client anchor), body `1c000000` then `0x2001..0x2004` + int32 = left/top/right/bottom in EMU (12,700/pt) **relative to the page centre**. Rotation adds one FOPT property; colour changes the FOPT fillColor. | Shape geometry and styling are readable now, from documented records plus this small anchor. |
| **Private parts** | Page list, shape positions/z-order, character/paragraph formatting, colour scheme and tables are in Publisher-private chunks (`Contents`, Quill). | This is the real work. **libmspub** (MPL-2.0) has already mapped most of it (block IDs for sizes, crop, text formatting, paragraphs, lists, tables, colours, embedded fonts). |
| **libmspub's failures are mostly conversion gaps** | LibreOffice drew the WordArt as red bars, yet the WordArt text, font and style *are* in the documented OfficeArt properties. | We don't have to beat libmspub at decoding the format, only at using what it decodes. |
| **We have an oracle** | Publisher (on the workstation until October 2026, then the PubOracle VM) gives exact ground truth: `Export-PubLayout.ps1` (element-level JSON) and page renders at 300 dpi. Scripts can generate any number of `.pub` files. | Differential reverse engineering: change one property, save, diff the bytes. Every decoder change is scored automatically against Publisher's own render. libmspub never had this harness. |

## Competition (the business risk, larger than the technical one)

- **Commercial converters and editors**: a few paid or freemium tools now advertise opening `.pub` files, some in
  the browser. We have not measured them; none publishes its format knowledge.
- **LibreOffice / Scribus**: free, using libmspub. Fine for simple files, poor for richly designed pages (our measurements: 1–6 vs 10–64).
- Demand peaks now (October 2026) and falls as people convert or give up. A rescue product that ships in six
  months arrives after the peak.

## Proposed spike (2 weeks) and the go/no-go gate

1. **C# reader for the 2003+ family**, written from MS-CFB and MS-ODRAW plus our own differential analysis,
   with libmspub's source studied as a reference (and described in our own words, never copied). Ported libmspub code, if any, stays in separate
   MPL-2.0 files.
2. Scope: pages and sizes, shape positions/sizes/rotation/z-order, fills/lines/shadows, pictures with crop,
   text boxes with character and paragraph formatting, WordArt as styled text with warp presets.
3. **Measure** on the 36 real files and the 15 feature files with the existing fidelity harness. Same metric;
   Publisher's renders are the reference; the Publisher-assisted export (WI-002) is the comparison.

**GO** (build the rescue product) if the native reader reaches score ≤ 3.0 on ≥ 80 % of real-file pages,
with no silent losses (anything unsupported is flagged), within the 2 weeks.
**NO-GO** (pivot to user-facing feature parity, keeping the Publisher-assisted export for our own files) if
it doesn't, or if measurement shows existing tools already reach that fidelity at a price we
can't beat.

Either way, what we build isn't wasted. The document model, renderer, fidelity harness and oracle are needed
for both paths.
