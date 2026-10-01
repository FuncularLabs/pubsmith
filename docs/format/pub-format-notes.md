# Microsoft Publisher .pub format notes (2002/2003–2021 family)

Notes for Pubsmith. They describe the file format as libmspub (LibreOffice, MPL-2.0) reads it. They were written by reading libmspub's source (`src/lib`, master at commit `2b09424`, 2026-09-23) and restating the behaviour in our own words. No libmspub code is reproduced. The identifier names and numeric constants below are facts about the format.

Conventions:

- All integers are **little-endian**. `u8/u16/u32` are unsigned and `s16/s32` are signed.
- **EMU** is the English Metric Unit: 914,400 per inch, 12,700 per point.
- **16.16** is a fixed-point value in a 32-bit word. The high 16 bits are a signed integer part and the low 16 bits are the fraction (/65536).
- **INFERRED** marks a statement that we reasoned out from how the code behaves. The code does not state these directly. Everything else is read straight from the code.
- **BUG?** marks places where libmspub's behaviour looks wrong or lossy. Our reader should not copy it blindly.
- libmspub only reads the format. Nothing here says what Publisher needs when it *writes* a file.

---

## 0. Container and version detection

A `.pub` file is an OLE2 / CFB compound file. libmspub uses these streams:

| Stream path | Used for |
|---|---|
| `Contents` | Document structure: pages, shape records, palette, table metadata, BorderArt, embedded fonts |
| `Quill/QuillSub/CONTENTS` | All text, character and paragraph formatting, font table, text colour table |
| `Escher/EscherStm` | OfficeArt (Escher) drawing records: geometry, anchors, fills, lines, z-order, grouping |
| `Escher/EscherDelayStm` | Image blobs (BLIPs) referenced from the BStore |
| `\x05SummaryInformation`, `\x05DocumentSummaryInformation` | Standard OLE property-set metadata (optional) |

Version detection uses the first 4 bytes of `Contents`:

| Bytes | Meaning in libmspub |
|---|---|
| `E8 AC 2C 00` | "2k2" format. Publisher 2002 and later, which includes 2003–2021. Parsed by `MSPUBParser`. It needs `Escher/EscherStm` and `Quill/QuillSub/CONTENTS`. |
| `E8 AC 22 00` | "2k" family. If `Quill/QuillSub/CONTENTS` exists, the file is Publisher 2000 (`MSPUBParser2k`). If not, it is Publisher 97/98 (`MSPUBParser97`). |
| anything else | unsupported |

Parse order in the 2k2 parser matters because later steps depend on earlier ones:

1. Metadata.
2. **Quill**, which builds the text stories, keyed by text id.
3. **Contents**, which builds pages and shape records, keyed by *seqnum*.
4. **EscherDelayStm**, which builds the image list.
5. **EscherStm**, which builds geometry, fill, line and z-order and links everything by seqnum.

A missing Quill, Contents or EscherStm stream is a hard failure. A missing EscherDelayStm is not.

---

## 1. `Contents` stream (2k2+)

### 1.1 Header

| Offset | Size | Meaning |
|---|---|---|
| 0x00 | 4 | Magic `E8 AC 2C 00` |
| 0x1A | u32 | **Trailer offset**: absolute offset of the trailer within `Contents` |

libmspub reads nothing else from the header. The bytes between 0x04 and 0x1A are unknown.

### 1.2 The generic "block" encoding

Nearly everything in `Contents`, and every Quill property record, is a sequence of **blocks**:

```
u8  id      -- meaning depends on context (the same id means different things in different containers)
u8  type    -- determines the payload size (see table)
... payload
```

The payload size is set by the `type` byte:

| type byte(s) | Payload |
|---|---|
| 0x78 (`DUMMY`), 0x05, 0x08, 0x0A | none (0 bytes). A presence flag. |
| 0x07, 0x10, 0x12, 0x18, 0x1A | 2 bytes, read as u16 |
| 0x20, 0x22, 0x58, 0x68, 0x70 (`SHAPE_SEQNUM`), 0xB8 | 4 bytes, read as u32 |
| 0x28 | 8 bytes (libmspub skips the contents) |
| 0x38 | 16 bytes (skipped) |
| 0x48 | 24 bytes (skipped) |
| 0x80, 0x82, 0x88 (`GENERAL_CONTAINER`), 0x8A, 0x90 (`TRAILER_DIRECTORY`), 0x98, 0xA0 | **variable length**: see below |
| 0xC0 (`STRING_CONTAINER`) | variable length. The payload is raw string bytes, usually UTF-16LE. |
| anything else | libmspub treats it as 0 bytes and logs "unknown block type". This is a real gap: the stream desynchronises silently. |

**Variable-length blocks:** the first u32 of the payload is a **length that includes that u32 itself**. The block therefore occupies `2 + length` bytes. The payload after the length is either a string (type 0xC0: `length − 4` bytes) or a nested sequence of blocks (every other variable type). Walking a container means reading child blocks from `dataOffset + 4` until `dataOffset + length`, where `dataOffset` is the position just after the `id,type` pair.

Arrays follow a common pattern. A container holds child containers whose `id` is the element index, and usually every element uses `0`. Each child holds scalar blocks keyed by id. Code often skips the 4-byte length and then walks the children.

INFERRED: the type byte looks like a size class. Bit 7 set means variable length; 0x78 is an empty placeholder element; the others are fixed-size integers. We have not seen 1-byte fixed blocks in libmspub's table. If an unknown type turns up, our reader should fail loudly rather than assume 0.

### 1.3 Trailer and the chunk directory

At the trailer offset:

```
u32 trailerLength
block x3            -- libmspub parses exactly three blocks here
```

Only the block whose `type == 0x90` (TRAILER_DIRECTORY) is used. The other two are unknown. The directory's children are a flat list of blocks. **Each child's 0-based index in this list is its `seqnum`.** The counter advances for *every* child block, including blocks that are not chunk references. Seqnums are therefore positions in the directory, not stored values.

A child of type 0x88 (GENERAL_CONTAINER) is a **chunk reference**. Its sub-blocks are:

| sub-block id | Meaning |
|---|---|
| 0x02 (`CHUNK_TYPE`) | Content chunk type (table below) |
| 0x04 (`CHUNK_OFFSET`) | Absolute offset of the chunk data in `Contents` |
| 0x05 (`CHUNK_PARENT_SEQNUM`) | Seqnum of the parent chunk (optional; defaults to 0) |

A reference counts only if it has both a type and an offset. The end of each chunk is taken to be the offset of the next registered chunk, or the end of the directory for the last one. libmspub never actually needs the end, because each chunk carries its own length.

Content chunk types (`MSPUBContentChunkType`):

| Value | Name | Handling in libmspub |
|---|---|---|
| 0x01 | SHAPE | shape record |
| 0x10 | TABLE | table shape record (see §5) |
| 0x20 | ALTSHAPE | treated as a shape. Its seqnum is recorded as "alternate" but **never used**. |
| 0x30 | GROUP | group record (only the Escher side matters) |
| 0x31 | LOGO | treated like GROUP |
| 0x43 | PAGE | page record |
| 0x44 | DOCUMENT | document record (exactly one; the parse fails without it) |
| 0x46 | BORDER_ART | BorderArt image library |
| 0x5C | PALETTE | document colour palette / scheme |
| 0x63 | CELLS | table cell-span table |
| 0x6C | FONT | embedded (EOT) fonts |
| other | unknown | recorded and ignored |

A chunk body at its offset is laid out as:

```
u32 length          -- measured from the chunk start (includes this u32)
block ...           -- until chunkOffset + length
```

libmspub processes the chunk types in this order: palettes, BorderArt, shapes (including tables, groups and logos), fonts, document, pages.

### 1.4 DOCUMENT chunk (0x44)

Top-level blocks:

| id | Meaning |
|---|---|
| 0x12 (`DOCUMENT_SIZE`) | Container. Child id 0x01 = **page width in EMU**; child id 0x02 = **page height in EMU**. |
| 0x02 (`DOCUMENT_PAGE_LIST`) | Container (array). Each child with id 0x00 holds a **page seqnum**. Together they give the **reading order of pages**. |

Every page in a document has the same size, and there is no per-page size. If the page list is empty, libmspub falls back to writing pages in ascending seqnum order.

### 1.5 PAGE chunk (0x43)

A page's identity is **its own seqnum**, meaning the seqnum of the chunk reference that points at it. Blocks:

| id | Meaning |
|---|---|
| 0x0A (`PAGE_BG_SHAPE`) | Seqnum of a **background shape**. Its Escher fill is painted over the whole page, behind everything. |
| 0x02 (`PAGE_SHAPES`) | Container. Each child block of **type 0x70** (`SHAPE_SEQNUM`, u32) is the seqnum of a **top-level** shape or group on this page. libmspub keys on the type byte here, not the id. |
| 0x0E (`THIS_MASTER_NAME`) | String (type 0xC0). A non-empty value (any non-zero byte) marks this page as **a master page**. |
| 0x0D (`APPLIED_MASTER_NAME`) | Integer (despite the name). The **seqnum of the master page applied to this page**. |

Master pages:

- A page is a master if it has a non-empty `THIS_MASTER_NAME`. Masters are not emitted as output pages.
- A normal page links to its master with block 0x0D. When a page is rendered, the layers go in this order:
  1. master background
  2. page background
  3. master shapes
  4. page shapes
- libmspub only uses the master if it exists as a page and is flagged as a master.

**Dummy pages:** seqnums 0x10D, 0x110, 0x113 and 0x117 are hard-coded as "dummy" pages and are never created. INFERRED: these are special internal pages. The meaning is unknown, but candidates are the scratch area and placeholders. Hard-coding seqnums is fragile. See §7.

Shape-to-page assignment: `PAGE_SHAPES` maps shape seqnum to page seqnum. Only top-level Escher elements (shapes or group leaders) are looked up. A group's children follow the group. Escher elements whose seqnum no page lists are dropped. INFERRED: this is how scratch-area objects are discarded.

### 1.6 SHAPE / ALTSHAPE / GROUP / LOGO chunks

Shape chunk blocks. All are optional.

| id | Meaning |
|---|---|
| 0xAA (`SHAPE_WIDTH`) | width in EMU. Read but not used; the Escher anchor is authoritative. |
| 0xAB (`SHAPE_HEIGHT`) | height in EMU. Read but not used. |
| 0x27 (`SHAPE_TEXT_ID`) | **Text id**. It links the shape to a Quill story (see §4). The shape is a text box if present. Ignored for GROUP/LOGO. |
| 0x09 (`SHAPE_BORDER_IMAGE_ID`) | **BorderArt index** into the BORDER_ART library (see §5.3) |
| 0x07 (`SHAPE_DONT_STRETCH_BA`) | Presence means "tile BorderArt pictures, don't stretch them". Absence means stretch. |
| 0x35 (`SHAPE_VALIGN`) | Vertical text alignment: 0 top, 1 middle, 2 bottom |
| 0xB7 (`SHAPE_CROP`) | If non-zero, an Escher **shape type** (MSOSPT) used in place of the Escher type for drawing. This is picture "crop to shape". It is not a crop rectangle. |

**Linking shape records to Escher:** the chunk's **seqnum** is the key. In Escher, each `SpContainer`'s `ClientData` record (0xF011) carries property **0x6801 = the seqnum**. There is no link through the Escher `spid`.

### 1.7 PALETTE chunk (0x5C)

```
u32 length
block ...  -- containers of type 0xA0; within each, children:
    type 0x88 (general container): a palette entry; its child with id 0x01 is a u32 colour 0x00BBGGRR
    type 0x78 (DUMMY):             an empty entry -> libmspub appends black (0,0,0)
```

Entries are appended in order to one global palette list, across all palette chunks if there is more than one. After parsing, **if the palette has fewer than 8 entries, black is inserted at index 0**. INFERRED: index 0 is an implicit "main/black" scheme slot that some files omit. This is a heuristic.

INFERRED: this palette holds the **colour-scheme colours**, which colour references with high byte 0x08 index (see §3). libmspub does not separate the scheme slots (main, accent 1–5, hyperlink, followed hyperlink and so on) from other palette entries, and it does not read scheme names.

### 1.8 FONT chunk (0x6C): embedded fonts

```
u32 length
block id 0x02 (FONT_CONTAINER_ARRAY): array; for each child id 0x00 (a container):
    child id 0x04 (EMBEDDED_FONT_NAME): string, UTF-16LE (a trailing 00 00 is dropped)
    child id 0x0C (EMBEDDED_EOT):       variable block; payload after its u32 length = EOT font bytes
```

Only fonts that have both a name and EOT data are kept. The EOT payload length libmspub reads equals the block length field, so it probably over-reads 4 bytes (**BUG?**).

---

## 2. Escher streams

### 2.1 Record header (standard OfficeArt, MS-ODRAW)

```
u16 verInstance   -- low 4 bits = version (0xF = container), high 12 bits = instance
u16 recType
u32 recLen        -- length of contents (after this 8-byte header)
```

Publisher-specific quirks libmspub handles:

- **A 4-byte tail follows `DggContainer` (0xF000) and `DgContainer` (0xF002).** libmspub skips 4 extra bytes after each of these containers ends. INFERRED: these are Publisher-specific trailer words; the content is unknown.
- **`ClientAnchor` (0xF010) and `ClientData` (0xF011) contents begin with a 4-byte value**, which libmspub treats as a repeat of the length. After it comes a list of `(u16 id, u32 value)` pairs until the record ends. An id of 0 does not end the list.

Record types used:

| recType | Name | Use |
|---|---|---|
| 0xF000 | DggContainer | Holds the BStore. Must come before any drawing that uses images. |
| 0xF001 | BStoreContainer | Array of FBSE entries (image table) |
| 0xF002 | DgContainer | One per drawing. libmspub walks every one. INFERRED: one per page, or one for the whole document; it does not matter to libmspub because the link is by seqnum. |
| 0xF003 | SpgrContainer | Group container (the top level and nested groups) |
| 0xF004 | SpContainer | One shape |
| 0xF009 | FSPGR | Group coordinate system: 4 × u32 (xs, ys, xe, ye). Normalised so that xs ≤ xe and ys ≤ ye. |
| 0xF00A | FSP | Shape: **instance = shape type** (MSOSPT); contents = u32 spid, u32 flags |
| 0xF00B | FOPT | Primary property table |
| 0xF122 | TertiaryFOPT | Tertiary property table (per-side borders, columns, recolour) |
| 0xF00F | ChildAnchor | 4 × u32 in the parent group's coordinate system |
| 0xF010 | ClientAnchor | Absolute anchor, Publisher format (see §2.4) |
| 0xF011 | ClientData | Publisher link data. Id **0x6801 = Contents seqnum** of the shape. |
| 0xF01A–0xF01F, 0xF029, 0xF02A | BLIPs | Only in EscherDelayStm (see §2.7) |

FSP flag bits (u32 at contents+4):

| Bit | Flag |
|---|---|
| 0x001 | group (this SpContainer is the **group leader**, i.e. the group's own properties and anchor) |
| 0x002 | child |
| 0x004 | patriarch |
| 0x008 | deleted |
| 0x010 | OLE shape |
| 0x020 | haveMaster |
| 0x040 | **flipH** |
| 0x080 | **flipV** |
| 0x100 | connector |
| 0x200 | haveAnchor |
| 0x400 | background |
| 0x800 | haveSpt |

### 2.2 Walk order, grouping and z-order

1. Find the `DggContainer`, then its `BStoreContainer`, and build the pib → image index map (§2.7). Skip the DGG and its 4-byte tail.
2. For every `DgContainer`, and for every `SpgrContainer` directly inside it, walk the group recursively. libmspub caps the recursion at depth 100.
   - A child `SpgrContainer` opens a nested group.
   - A child `SpContainer` is a shape. The **first** SpContainer of a group has the FSP group flag; it is the group leader. Its ClientData 0x6801 gives the **group's seqnum**, and its anchor gives the group's absolute rectangle.
3. **Z-order is Escher document order.** Shapes are recorded in the order met, and painted in that order: first is bottom-most. Groups keep their children in encounter order. A page's content is the top-level Escher elements whose seqnums appear in that page's `PAGE_SHAPES`, still in Escher order. The order of the seqnums inside `PAGE_SHAPES` is not used for z-order.
4. An SpContainer with no `ClientData`/0x6801 is ignored, and so is one with no anchor that is not a group leader.

### 2.3 Shape type

The FSP instance (bits 4–15 of verInstance) is the MSOSPT shape type. libmspub's enum matches the standard MSOSPT numbering. Examples:

| Value | Shape |
|---|---|
| 0 | not primitive (custom geometry from FOPT vertices/segments) |
| 1 | rectangle |
| 2 | round rectangle |
| 3 | ellipse |
| 20 | line |
| 24 | text simple |
| 32–40 | connectors |
| 75 | picture frame |
| 100 | "custom" |
| 136–175 | WordArt text-path shapes (TEXT_PLAIN_TEXT … TEXT_CAN_DOWN) |
| **202** | **text box** |

libmspub treats only types 1 (rectangle) and 202 (text box) as rectangles for border-inset logic. The built-in geometry for the preset shapes comes from libmspub's own tables (`PolygonUtils.cpp`), which are transcriptions of the standard preset definitions. We should use the standard MS-ODRAW / VML preset definitions, not those tables.

### 2.4 Anchors and coordinates

**ClientAnchor (0xF010)**, which the empirical finding confirms:

```
u32  (repeat of length / unknown)
then repeated: u16 id, s32 value
   0x2001 = xs (left)
   0x2002 = ys (top)
   0x2003 = xe (right)
   0x2004 = ye (bottom)
```

- Values are **EMU relative to the page centre**. libmspub computes the absolute position in inches as `pageWidthIn/2 + xs/914400`, and the same for y.
- libmspub reads the values as u32 and stores them as signed int, so they are signed in practice. Negative means left of or above centre.
- The rectangle is normalised (swapped if xs > xe or ys > ye).
- A value that is absent defaults to 0.

INFERRED: the page centre is the origin, so coordinates of objects on the scratch area fall outside ±width/2 and ±height/2.

**ChildAnchor (0xF00F)** is used for shapes inside groups. It holds 4 × u32 (xs, ys, xe, ye) in the **group's coordinate system**. That system is the FSPGR rectangle from the group leader. The code maps each value to absolute EMU:

```
absX = (childX − groupCS.xs) × (groupAbs.width / groupCS.width) + groupAbs.xs
absY = (childY − groupCS.ys) × (groupAbs.height / groupCS.height) + groupAbs.ys
```

Here `groupAbs` is the group leader's absolute anchor, which is its ClientAnchor or its own resolved child anchor for nested groups. A zero-width or zero-height coordinate system is replaced by 1. INFERRED: children of a group probably use ChildAnchor rather than ClientAnchor. libmspub accepts either and uses whichever it finds first.

**Rotation swaps the anchor.** If the shape's rotation (normalised to [0,360)) is in [45,135) or [225,315), the stored anchor rectangle is the **unrotated box with width and height exchanged about its centre**. libmspub rebuilds the real box as a rectangle of the same centre with width and height swapped, then applies the rotation. This matches standard Escher behaviour, and our reader must do the same.

**Rotation and flips:**

- FOPT 0x0004 holds the rotation in 16.16 fixed **degrees**. libmspub reduces it mod 360 and truncates to an integer before storing, which loses precision (**BUG?**; keep the fraction).
- Flips come from FSP flags. When exactly one flip is set, libmspub reverses the rotation direction.
- Group transforms compose: a child's transform is folded with its parent group's rotation and flip about the group centre.
- INFERRED: positive rotation is clockwise on screen, per MS-ODRAW.

### 2.5 FOPT parsing

- `FOPT.instance` = number of properties N.
- The table is N × 6 bytes: `u16 pid, u32 value`.
  - pid bit 0x8000 (`fComplex`) means complex: `value` is the byte length of an extra data block.
  - pid bit 0x4000 (`fBid`) means the value is a BLIP id.
  - libmspub keeps the **full 16-bit pid including these flag bits** as the key. That is why ids below look like 0x4104 and 0xC145.
- The complex data blocks follow the table **in the order the complex pids appeared**.
- Complex property layout, as libmspub reads it for arrays:

```
u16 nElems
u16 nElemsAlloc   (ignored)
u16 cbElem        (0xFFF0 means 4 -- the MS-ODRAW "half-size" convention)
nElems × cbElem bytes
```

  The reader takes `6 + nElems×cbElem` bytes. It does not use the declared complex length (**BUG?**: our reader should use the declared length to step to the next block).
- Duplicate pids: the last one wins.

### 2.6 FOPT property ids libmspub reads

Colour values below are **colour references** (§3). "Default" is libmspub's default when the property is absent.

**Transform / text box**

| pid | Name | Units / meaning |
|---|---|---|
| 0x0004 | rotation | 16.16 degrees |
| 0x0081 | dxTextLeft | EMU. Text-box inner margin. Default 36,576 EMU (0.04 in). |
| 0x0082 | dyTextTop | EMU, default 36,576 |
| 0x0083 | dxTextRight | EMU, default 36,576 |
| 0x0084 | dyTextBottom | EMU, default 36,576 |
| 0x008C | (tertiary) number of text columns | count |
| 0x008D | (tertiary) column spacing | EMU |

**Picture**

| pid | Name | Units / meaning |
|---|---|---|
| 0x4104 | pib | 1-based index into BStore → image (§2.7) |
| 0x0108 | pictureContrast | raw int; stored, not rendered |
| 0x0109 | pictureBrightness | raw signed int; libmspub maps it to luminance `(v + 32768) / 65536` |
| 0x011A | (tertiary) picture recolour | colour ref; renders as greyscale with that tint |

**Geometry**

| pid | Name | Units / meaning |
|---|---|---|
| 0x0142 | geoRight | width of the geometry coordinate space, default 21,600 |
| 0x0143 | geoBottom | height, default 21,600 |
| 0x0147–0x0149 | adjustValue 1–3 | s32 preset adjust handles. Only the first 3 are read; MS-ODRAW defines 10 (0x0147–0x0150). |
| 0xC145 | pVertices | complex: vertex array. cbElem 2 = 1-byte x,y; 4 = u16 x,y; 8 = u32 x,y. |
| 0xC146 | pSegmentInfo | complex: array of u16 segment commands (element size assumed to be 2) |
| 0xC156 | pGuides | complex: formulas. **Not parsed** (see §7). |
| 0xC383 | pWrapPolygonVertices | complex vertex array. libmspub uses it as a **clip path** in the geometry space. |
| 0x017F | geometry boolean props | bit 12 fUseLineOK, bit 28 fLineOK |

**Fill**

| pid | Name | Units / meaning |
|---|---|---|
| 0x0180 | fillType | 0 solid, 1 pattern, 2 texture, 3 picture, 4 shade (linear), 5 shade-centre, 6 shade-shape, 7 shade-scale, 8 shade-title, 9 background. Default solid. |
| 0x0181 | fillColor | colour ref |
| 0x0182 | fillOpacity | 16.16; libmspub divides by 0xFFFF. Default 1. |
| 0x0183 | fillBackColor | colour ref |
| 0x0184 | fillBackOpacity | 16.16 |
| 0x018B | fillAngle | 16.16 degrees. libmspub uses only the integer part and remaps −135→−45 and −45→225. It admits it does not understand the logic. |
| 0x018C | fillFocus | signed percent (−100..100; low 16 bits) |
| 0x018D–0x0190 | fillToLeft/Top/Right/Bottom | 16.16 fractions of the box (focus rectangle for shade-centre) |
| 0x4186 | fillBlip | 1-based BStore index for texture/picture/pattern fills |
| 0xC197 | fillShadeColors | complex: nElems entries of 8 bytes = `u32 colour, 16.16 position (0..1)` |
| 0x01BF | fill-style boolean props | If present and `(value & 0xF0) == 0`, libmspub treats the shape as unfilled (INFERRED: the fUse*/fFilled pair, with fFilled off). |

**Line**

| pid | Name | Units / meaning |
|---|---|---|
| 0x01C0 | lineColor | colour ref |
| 0x01C2 | lineBackColor | colour ref. Also the colour applied to 1-bit BorderArt pictures. −1 means none. |
| 0x01CB | lineWidth | EMU, default 9,525 (0.75 pt) |
| 0x01CE | lineDashing | 0 solid, 1 dashSys, 2 dotSys, 3 dashDotSys, 4 dashDotDotSys, 5 dotGEL, 6 dashGEL, 7 longDashGEL, 8 dashDotGEL, 9 longDashDotGEL, 10 longDashDotDotGEL |
| 0x01D0 / 0x01D1 | line start / end arrowhead | 0 none, 1 triangle, 2 stealth, 3 diamond, 4 oval, 5 open |
| 0x01D2 / 0x01D3 | start arrow width / length | 0 small, 1 medium, 2 large |
| 0x01D4 / 0x01D5 | end arrow width / length | 0 small, 1 medium, 2 large |
| 0x01D7 | lineEndCapStyle | 0 = round (dots rendered round), else flat |
| 0x01FF | line boolean props | bit 19 fUseLine, bit 3 fLine. The line is present unless (fUseLine set and fLine clear), and it is also gated by the geometry flags above. |

**Per-side borders** (TertiaryFOPT, used when the primary FOPT has no lineColor but tertiary line bool 0x01FF says there is a line)

| Side | colour pid | width pid (EMU) | bool pid |
|---|---|---|---|
| top | 0x0580 | 0x058B | 0x05BF |
| left | 0x0540 | 0x054B | 0x057F |
| right | 0x05C0 | 0x05CB | 0x05FF |
| bottom | 0x0600 | 0x060B | 0x063F |

- libmspub stores the lines in the order top, right, bottom, left.
- The left bool prop also sets the border position. The border sits inside the shape if bit 22 (fUseInsetPen) is set, bit 6 (fInsetPen) is set, and either bit 21 (fUseInsetPenOK) is clear or bit 5 (fInsetPenOK) is set. Otherwise the border straddles the edge (half inside).

**Shadow** (applied only if 0x023F has both bit 17 fUseShadow and bit 1 fShadow)

| pid | Name | Units / default |
|---|---|---|
| 0x0200 | shadowType | 0 offset, 1 double, 2 rich, 3 shape, 4 drawing, 5 emboss/engrave. libmspub renders only type 0. |
| 0x0201 | shadowColor | colour ref, default 0x808080 |
| 0x0202 | shadowHighlight | colour ref, default 0xCBCBCB |
| 0x0204 | shadowOpacity | 16.16, default 1.0 |
| 0x0205 / 0x0206 | shadowOffsetX / Y | EMU (signed), default 0x6338 = 25,400 (2 pt) |
| 0x0207 / 0x0208 | second offset X / Y | EMU |
| 0x0210 / 0x0211 | shadow origin X / Y | 16.16 |

**Not read by libmspub** (we must reverse-engineer or take them from MS-ODRAW):

- all text-path / WordArt (`gtext*`, 0x00C0–0x00FF)
- **picture crop** (cropFromTop/Bottom/Left/Right, 0x0100–0x0103)
- 3-D
- connector rules
- hyperlinks
- `wzName` / `wzDescription`
- the lineStyle compound type (0x01CD, thin-thick etc.)
- line join
- pattern definitions beyond the BLIP
- the remaining adjust values
- `pGuides`

### 2.7 Images: BStore and EscherDelayStm

**BStore** (inside the DGG): libmspub walks the FBSE records as fixed **44-byte** steps (8-byte header + 36-byte FBSE body). It assumes no name and no embedded BLIP (**BUG?**: our reader should use each record's own length). For each entry it looks at the 16 bytes at record+10, which is the `rgbUid` of the FBSE.

- If the 16 bytes are not all zero, the entry is given the next **delay index** (1, 2, 3, …).
- If they are all zero, the entry is empty (−1).

FOPT `pib` / `fillBlip` = n (1-based) means BStore entry n, which maps to that delay index. That index is the n-th BLIP in `EscherDelayStm` counting from 1 in stream order, including BLIPs of unknown type.

INFERRED: the correct method is FBSE `foDelay`, the offset of the BLIP in the delay stream. libmspub's positional mapping holds only while the delay stream order matches the BStore order.

**EscherDelayStm** is a sequence of BLIP records (standard 8-byte header). The image data starts at a fixed offset inside the record contents. That offset depends on the type and on whether the record has one UID or two:

| recType | Format | Data offset (one UID) | recInstance values meaning "one UID" |
|---|---|---|---|
| 0xF01A | EMF | 0x34 | 0x3D4 |
| 0xF01B | WMF | 0x34 | 0x216 |
| 0xF01C | PICT | 0x11 | (default) |
| 0xF01D | JPEG | 0x11 | 0x46A, 0x6E2 |
| 0xF01E | PNG | 0x11 | 0x6E0 |
| 0xF01F | DIB | 0x11 | 0x7A8 |
| 0xF029 | TIFF | 0x11 | 0x6E4 |
| 0xF02A | JPEG (CMYK) | 33 | 0x46B, 0x6E3 |

When a record has two UIDs, add 0x10 to the offset.

- **WMF and EMF data is zlib-deflated.** Inflate it. The 0x34 header is UID(16) plus the metafile header (34 bytes).
- **DIB** has no BITMAPFILEHEADER. libmspub builds one. The bits offset is `0x36 + 4 × paletteColours`, and when the palette count is 0 and bpp ≤ 8 it is `2^bpp`.
- libmspub reads `recLen` bytes starting at the data offset, which over-reads into the next record by the header size (**BUG?**; read `recLen − dataOffset`).
- **Pattern fills** are DIB BLIPs. libmspub replaces their two palette entries with the fill's foreground and background colours.

---

## 3. Colour references

Every colour in Escher properties, the palette and Quill text colours is a u32 **colour reference**:

| High byte (`c >> 24`) | Meaning |
|---|---|
| 0x00 (and anything else not listed) | Literal RGB: `R = c & 0xFF`, `G = (c >> 8) & 0xFF`, `B = (c >> 16) & 0xFF` |
| 0x08 | **Palette / scheme index**: `c & 0xFFFFFF` indexes the palette built from the PALETTE chunk(s), after the "insert black at 0 if < 8" fix-up. Out of range means black. |
| 0x10 | **Modified colour (tint/shade)**. Used as the *second* colour of a gradient. The base colour is another reference (the gradient's fill colour). Byte 1, `(c >> 8) & 0xFF`, is the base: 0x01 = mix toward **black** (shade), 0x02 = mix toward **white** (tint). Byte 2, `(c >> 16) & 0xFF`, is the intensity t = byte/255. Shade gives `rgb × t`. Tint gives `rgb + (255 − rgb) × (1 − t)`. Any other base gives black. |

- libmspub applies the 0x10 form only where it pairs a base with a modified reference, which is gradient second colours.
- INFERRED: Publisher also uses the 0x10 form for scheme-colour tints elsewhere, such as fills with "tint of accent 1". libmspub would render those as black. This is a likely gap.
- Other MS-ODRAW high-byte flags (0x01 fPaletteIndex, 0x02 fPaletteRGB, 0x04 fSystemRGB, fSchemeIndex) are not handled.

**Default colours:**

- Text with no colour is black.
- A shade fill with no fillColor starts white (0xFFFFFF). Its back colour defaults to white.
- A pattern fill defaults to white on white.

**Quill text colours:** see §4.6 `PL  `. Each entry is a colour reference as above. A character style's colour index selects an entry from that list.

---

## 4. Quill (`Quill/QuillSub/CONTENTS`)

### 4.1 Chunk directory

The directory is a linked list of pages. It starts with the first page at offset **0x18**:

```
at pageOffset:
  u16  (unknown)
  u16  numChunks
  u32  nextPageOffset      -- 0xFFFFFFFF terminates; libmspub guards against cycles
  numChunks × 24-byte entries:
     u16  (normally 0x0018)
     char[4] name          -- e.g. "TEXT", "STSH", "FDPC", "FDPP", "SYID", "STRS", "PL  ", "FONT", "TCD "
     u16  id               -- instance id (distinguishes multiple chunks of the same name)
     u32  (normally 0x00000001; unknown)
     char[4] name2         -- secondary tag (unknown use)
     u32  offset           -- absolute offset in the Quill stream
     u32  length
```

Chunks libmspub uses:

| Name | Purpose |
|---|---|
| `TEXT` | All story text, concatenated, **UTF-16LE** |
| `STRS` | Story lengths |
| `SYID` | Story → text id |
| `STSH` | Style sheet. Two instances; only the **second** is read. |
| `FDPC` | Character formatting runs. Can be several chunks; they are concatenated in directory order. |
| `FDPP` | Paragraph formatting runs. Several chunks, concatenated. |
| `PL  ` | Text colour list |
| `FONT` | Font name table |
| `TCD ` | Table cell text boundaries |

libmspub builds text only if STRS, SYID, FDPC, FDPP, STSH, FONT and TEXT are all present. Otherwise the document has no text.

### 4.2 Stories: STRS, SYID, TEXT

**STRS:**

```
u32 numStories
u32 skip            -- libmspub seeks to (chunkOffset + 4 + skip) for the array (i.e. it's the byte distance to the array from +4)
numStories × u32 storyLength   -- in UTF-16 code units
```

**SYID:**

```
u32 (unknown)
u32 numIds
numIds × u32 textId
```

**TEXT:** the stories follow each other in STRS order. Story *j*:

- has `storyLength[j]` UTF-16 code units
- starts at byte `sum(storyLength[0..j−1]) × 2` from the TEXT chunk start
- has text id `SYID[j]`

A shape's `SHAPE_TEXT_ID` (Contents id 0x27) equals one of these SYID values. That is the **text id → story** link.

- INFERRED: several text boxes that are linked in a chain share one story. libmspub has no support for linking or overflow. It puts the whole story into the one shape that references it. See §7.
- Paragraphs end with U+000D.
- libmspub converts U+0009 to a tab and U+000A to a line break.
- Other control characters in the text are passed through. INFERRED: U+000B may be a line break and there may be field codes. libmspub does not handle these.

### 4.3 FDPC / FDPP layout (formatting runs)

The two chunks have the same structure:

```
u16  numEntries
6 bytes (unknown)
numEntries × u32  textEnd       -- see below
numEntries × u16  propOffset    -- offset of the property record, relative to the chunk start
```

Each property record at `chunkOffset + propOffset` is:

```
u32 length   -- includes itself
block ...    -- property blocks (same encoding as §1.2), until record start + length
```

- Run *i* covers text up to `textEnd[i]`.
- libmspub compares `textEnd` with `(bytes consumed so far in TEXT) + textChunkOffset`. So **`textEnd` is an absolute byte offset in the Quill stream** marking where the run ends (INFERRED: exclusive end, the offset just after the run's last code unit).
- Runs are consecutive and cover the text with no gaps.
- **BUG?**: libmspub stores these ends in 16-bit fields, so it breaks on Quill streams larger than 64 KB. Use 32-bit values.

INFERRED: the name and layout look like the Word "FKP" pages (formatted disk pages). Big documents may split runs across several FDPC/FDPP chunks, which is why they are concatenated.

Story boundaries do not break runs. The run pointers carry on across stories, and a run can straddle two stories. libmspub splits text into spans at run ends, and into paragraphs at FDPP ends.

### 4.4 Character properties (FDPC records; STSH even entries)

| Block id | Name | Meaning |
|---|---|---|
| 0x02 | BOLD_1 | **Presence means bold.** libmspub ignores the value. |
| 0x37 | BOLD_2 | Seen in 2007+ files. Ignored. |
| 0x03 | ITALIC_1 | Presence means italic |
| 0x38 | ITALIC_2 | Ignored |
| 0x1E | UNDERLINE | Low byte: 0 none, 1 single, 2 words only, 3 double, 4 dotted, 6 thick, 7 dash, 9 dot-dash, 0x0A dot-dot-dash, 0x0B wave, 0x10 thick wave, 0x11 thick dot, 0x12 thick dash, 0x13 thick dot-dash, 0x14 thick dot-dot-dash, 0x15 long dash, 0x16 thick long dash, 0x17 double wave. Unknown values are read as single. |
| 0x0C | TEXT_SIZE_1 | **Font size in EMU** (12,700 per point), not half-points. |
| 0x39 | TEXT_SIZE_2 | Unknown second size. Ignored. |
| 0x2E | BARE_COLOR_INDEX | Integer index into the `PL  ` colour list |
| 0x44 | COLOR_INDEX_CONTAINER | Container. The child with id 0x00 holds the colour index into `PL  `. |
| 0x24 | FONT_INDEX_CONTAINER | Container. Its first child general container (type 0x88) holds a first sub-block whose integer is the **font index** into the `FONT` table. |
| 0x0F | SUPER_SUB_TYPE | 1 superscript, 2 subscript |
| 0x04 | OUTLINE | presence flag |
| 0x05 | SHADOW | presence flag |
| 0x13 | SMALL_CAPS | presence flag |
| 0x14 | ALL_CAPS | presence flag |
| 0x16 | EMBOSS | presence flag |
| 0x17 | ENGRAVE | presence flag |
| 0x20 | SCALING | Horizontal character scale: percent = value / 10 |
| 0x12 | LOCALE | Windows LCID |

How libmspub resolves character formatting against the style sheet:

- The run's style is compared with the paragraph's **default character style**, the STSH char style picked by the paragraph's id 0x19.
- Boolean flags are **XOR**ed with the default: bold in the run and bold in the default gives *not* bold. INFERRED: the run properties are toggles relative to the style, as in Word.
- Size, colour, font, underline, scale and locale fall back to the default when the run does not set them.
- If there is no font, the first font in the table is used.
- If there is no colour, black is used.

Features not read at all:

- character spacing, tracking and kerning
- highlight
- strikethrough (INFERRED: probably one of the unhandled ids)
- hidden text
- ligatures and OpenType features
- per-script fonts (east Asian and complex-script)

### 4.5 Paragraph properties (FDPP records; STSH odd entries)

| Block id | Name | Meaning |
|---|---|---|
| 0x04 | ALIGNMENT | Low byte: 0 left, 1 right, 2 centre, 6 justify. Other values are read as left. INFERRED: 3–5 are distributed or other justification modes. |
| 0x19 | DEFAULT_CHAR_STYLE | Style index. libmspub uses it as the index into **both** the STSH character list and the STSH paragraph list. |
| 0x34 | LINE_SPACING | **Bit 0 set means exact**: `points = (v − 1) / 8 / 914400 × 72`, i.e. `v = 8 × EMU + 1`. **Bit 1 set means multiple**: `lines = (v − 2) / 914400 × 72 / 96`, so 1.0 = single and `v = 2 + lines × 1,219,200`. INFERRED: v is 2 + (line height in EMU for a nominal 96 pt font). Absent means single. |
| 0x12 | SPACE_BEFORE | EMU |
| 0x13 | SPACE_AFTER | EMU |
| 0x0C | FIRST_LINE_INDENT | EMU, signed |
| 0x0D | LEFT_INDENT | EMU |
| 0x0E | RIGHT_INDENT | EMU |
| 0x08 | DROP_CAP_LINES | count |
| 0x2D | DROP_CAP_LETTERS | count |
| 0x2C | DROP_CAP_UP | Not used. INFERRED: raised-cap lines. |
| 0x32 | TABS | Container → child id 0x28 (tab array) → entries (type 0x88) → first sub-block id 0x00 = **tab position, EMU**. Tab alignment and leader are not read. |
| 0x57 | LIST_INFO | Container. Child 0x00 = numbering type (0 1,2,3; 1 I; 2 i; 3 A; 4 a; 5 ordinal; 6 cardinal text; 7 ordinal text; 0x16 01,02). Child 0x01 = bullet character (Unicode code unit). |
| 0x58 | numbering delimiter | Declared but **never read**. Values: 0 `)`, 1 `( )`, 2 `.`, 4 `[ ]`, 5 `:`, 6 `[ ]` surround, 7 `- -`, 8 ideographic comma. |
| 0x15 | LIST_NUMBER_RESTART | start number |

- **BUG?**: libmspub reads the list sub-values from the wrong variable (the outer container's value, which is always 0). As a result, bullet characters and numbering types are effectively never picked up. We must verify these against real files ourselves.
- A paragraph falls back to the STSH paragraph style at index 0x19 for any property it does not set.
- Not read:
  - keep-with-next and keep-together
  - widow control
  - hyphenation
  - rules and borders
  - shading
  - baseline alignment
  - bullet font and bullet size
  - list indent
  - east Asian settings

### 4.6 Other Quill chunks

**STSH** (the second instance only; libmspub counts occurrences and uses index 1):

```
u32 (unknown)
u32 numElements
12 bytes (unknown)
numElements × u32 offset      -- relative to (chunkOffset + 20), i.e. to the start of this offset table
each element at chunkOffset + 20 + offset:
    u16 (unknown)
    property record (u32 length + blocks)
```

- Even indices are **character styles**. Odd indices are **paragraph styles**. INFERRED: elements come in pairs, one char and one para per named style.
- The resulting lists are indexed by paragraph id 0x19.
- libmspub's own comment says it does not know how the two STSH chunks relate. The first may be the style names or definitions (INFERRED).

**FONT:**

```
u32 (unknown)
u32 numFonts
12 bytes + 4 × numFonts bytes (unknown; likely an offset table)
numFonts × {
    u16 nameLengthInChars
    UTF-16LE name (nameLength × 2 bytes; not NUL-terminated)
    u32 (unknown -- INFERRED: charset/pitch/family or flags)
}
```

The font index in char property 0x24 is an index into this list, starting from 0.

**`PL  `** (the text colour list):

```
u32 numEntries
8 bytes (unknown)
numEntries × { u32 length (incl. itself); blocks; child id 0x01 = colour reference (§3) }
```

**`TCD `** (table cell definitions):

- The chunk's `id` is the **story index** *j*, not the text id. It applies to story *j*.
- Layout:

```
u32 (count − 1)
at chunkOffset + 0x0C: count × u32 cellEnd
```

- Each `cellEnd` is a character offset in the story where a cell's text ends. libmspub adds 2 to every value except the last, to count the terminating U+000D that is not otherwise included.
- Cells are laid out in the order of the CELLS chunk entries (§5.1). INFERRED: row-major.

---

## 5. Tables, BorderArt, groups

### 5.1 Tables

A TABLE chunk (Contents type 0x10) is a shape record with these blocks:

| id | Meaning |
|---|---|
| 0x66 | number of rows |
| 0x67 | number of columns |
| 0x68 / 0x69 | table width / height (EMU). Declared but unused. |
| 0x6B | **seqnum of the CELLS chunk** |
| 0x6D | Row/column size array. Container whose children (id 0x00 containers) each hold id 0x01 (offset; unused) and id 0x02 (**size, EMU**). The first `numColumns` entries are **column widths**; the next `numRows` entries are **row heights**. |
| 0x27 | text id. The whole table's text is **one story**, split into cells by the `TCD ` chunk. |

If the counts do not match, libmspub rejects the table.

CELLS chunk (type 0x63), found by seqnum:

| id | Meaning |
|---|---|
| 0x01 | cell count |
| 0x02 | Array. Each id 0x00 container is one cell. Sub-ids: 0x01 startRow, 0x02 endRow, 0x03 startColumn, 0x04 endColumn. All are inclusive and 0-based, so merged cells span more than one row or column. |

Cell content is built as follows:

- The story's paragraphs are assigned to cells in cell-list order, using the TCD ends.
- A trailing lone U+000D span in each paragraph is dropped.
- A cell whose text ends in the middle of a paragraph is logged, not split.

Not read:

- cell borders
- cell fill
- cell margins (sub-ids 0x09–0x0E are suspected; libmspub has a TODO saying they may be content width/height plus margins)
- cell vertical alignment
- diagonal lines
- table styles and auto-format

The table's position and size come from the Escher anchor as usual.

### 5.2 Groups

- A group appears in `Contents` as a GROUP (0x30) or LOGO (0x31) chunk. Only its seqnum matters.
- In Escher it appears as an SpgrContainer whose first SpContainer is the leader. The leader has the FSP group flag, FSPGR with the group coordinate system, ClientData 0x6801 = the group seqnum, and a ClientAnchor or ChildAnchor.
- Children are laid out through ChildAnchor (§2.4).
- The page lists only the group seqnum. The children inherit the page.
- A group has no fill or line of its own in rendering. INFERRED: FOPT on the group leader may still carry properties.

### 5.3 BorderArt

**BORDER_ART chunk** (type 0x46):

```
u32 length
block id 0x02 (BA_ARRAY): array; element i (0-based, each a container) = BorderArt i:
    child id 0x0A (BA_IMAGE_ARRAY): array; each id 0x00 container holds a block id 0x01 (BA_IMAGE)
         whose payload is the picture bytes (libmspub assumes **WMF**, uncompressed)
    child id 0x08 (BA_OFFSET_CONTAINER): array of id 0x00 u32 values = picture offsets
```

INFERRED: libmspub copies `dataLength` bytes from `dataOffset + 4`, so it probably over-reads 4 bytes (**BUG?**).

A shape uses BorderArt when both of these are true:

- Contents id 0x09 gives a BorderArt index.
- The shape has a line.

How libmspub applies it:

- **The line width is the size of the BorderArt pictures**, so pictures are width × width squares.
- The 8 offsets are used in this order: top-left corner, top edge, top-right corner, right edge, bottom-right corner, bottom edge, bottom-left corner, left edge. Each offset picks a picture by its **rank** among the sorted distinct offsets, so the picture list is in ascending offset order. If there are fewer than 8 offsets, the last one is reused.
- Edge pictures are either stretched to fill a whole number of repeats (the default) or tiled with even gaps (when id 0x07 is present).
- 1-bit pictures take `lineBackColor` (0x01C2) as their colour.
- The fill is inset by one border width.
- libmspub first draws white rectangles under the border band.

INFERRED: the offsets are positions of the pictures in an original file. Several of the 8 slots can point to the same picture, as symmetric borders do.

### 5.4 Page background

`PAGE_BG_SHAPE` (id 0x0A) names a shape seqnum. That shape's fill (solid, gradient, picture, texture or pattern) is drawn as a rectangle covering the whole page, below all shapes. The master's background is drawn first.

### 5.5 Rendering-relevant details

- For rectangles and text boxes, the border straddles the edge by default. With "inset pen" it lies inside the edge. libmspub expands the stroke box and shrinks the text box by half the border width, or by the full width when the border is outside.
- A text box's padding is the four dx/dyText margins.
- Text columns are FOPT tertiary 0x008C (count) and 0x008D (gap).
- A shape rotation also rotates its text.
- Clip paths: the wrap polygon (0xC383) scaled into the shape box.

---

## 6. Custom geometry (vertices and segments)

- The geometry space runs from 0 to `geoRight` in x and 0 to `geoBottom` in y, default 21,600. It is scaled to the shape box.
- A vertex coordinate with bit 31 set is a **reference to formula (guide) number `v & 0x7FFFFFFF`**. libmspub cannot parse `pGuides` from files. It evaluates guides only for its built-in presets, which use formulas 0x00–0x10 and 0x80–0x82 in the standard MS-ODRAW sense.

Segment commands are u16 values. The high byte is the command and the low byte is the count:

| High byte | Command | Vertices consumed per count |
|---|---|---|
| 0x00, 0xAC, 0xAE | lineTo | 1 each |
| 0x20, 0xAD, 0xAF, 0xB3 | curveTo (cubic) | 3 each |
| 0x40 | moveTo | count 0 counts as 1 |
| 0x60 | close subpath | — |
| 0x80 | end subpath | — |
| 0xA2 | angleEllipse (centre, radii, start/sweep angles) | 3 per ellipse (count = lowByte / 3) |
| 0xA3 | arcTo | 4 each (count = lowByte / 4) |
| 0xA4 | arc | 4 each (count = lowByte / 4) |
| 0xA5 | clockwise arcTo | 4 each (count = lowByte / 4) |
| 0xA6 | clockwise arc | 4 each (count = lowByte / 4) |
| 0xA7 | ellipticalQuadrantX | 1 each |
| 0xA8 | ellipticalQuadrantY | 1 each |
| 0xAA | noFill | ignored |
| 0xAB | noStroke | ignored |

- Arc vertex groups are: bounding corner 1, bounding corner 2, start ray point, end ray point.
- If there is no segment data, the vertices are drawn as one closed polygon.

These encodings follow MS-ODRAW `MSOPATHINFO`. INFERRED: libmspub's mapping of some high bytes is looser than the specification. For example, 0xAC/0xAE/0xAD/0xAF/0xB3 are the "escape" forms, and in MS-ODRAW the escape code sits in bits 8–12 of 0xA000-class values. For our reader, **MS-ODRAW §2.2.x (MSOPATHINFO / MSOPATHESCAPE)** is the authority.

---

## 7. Known gaps, TODOs and FIXMEs in libmspub

These are where libmspub admits ignorance, or where its behaviour is clearly incomplete. They are our reverse-engineering backlog.

**Contents**

1. **Unknown block types** are treated as 0-length, which risks silent desync. The type→size table is incomplete. The `FIXME` says this "should never get here".
2. Most of the **`Contents` header** (0x04–0x19) and **two of the three trailer blocks** are not understood.
3. **Dummy pages** are identified by hard-coded seqnums (0x10D, 0x110, 0x113, 0x117 for 2k2; others for 2k). The real flag that marks a special or internal page is unknown.
4. **ALTSHAPE** (0x20) meaning is unknown. It is recorded and never used. INFERRED: an alternative representation, perhaps for web output or a compatibility copy.
5. **LOGO** (0x31) is treated as a plain group.
6. **Unknown chunk types** are ignored. They include all publication-level settings: layout guides, margins, baseline grid, sections, page numbering, fields, catalog merge, web settings, print settings, text styles by name and so on.
7. Shape width and height in Contents (0xAA/0xAB) are ignored. `FIXME` asks whether shapes without dimensions should be ignored.
8. **Text-box linking and overflow**: there is no support for connected text frames or for continuing a story across boxes. It is unknown how a Contents shape record records "next linked box" or the start offset of its text.
9. **Wrap settings** are ignored: text wrap type, wrap distance and per-shape "wrap text around".
10. **Page size variations and sections**: there is one global size, and none of the following are read: orientation, sections, mirrored/spread (facing pages), page numbering format.

**Colour**

11. **Colour-scheme structure**: scheme slot semantics and scheme names are not read. The palette index heuristic ("insert black if < 8") is guesswork. Tint/shade references (0x10) are handled only as gradient second colours. CMYK, spot colours and other colour models are not handled.

**Escher**

12. **Escher `pGuides`** (formulas in files) are not parsed: `parseGuides` is an explicit `FIXME` stub. Custom shapes that use guides render wrongly.
13. **Rotation precision**: stored as an integer, per `FIXME: make MSPUBCollector handle double shape rotations`.
14. **Fill angle mapping** is "arbitrary" by libmspub's own comment, and the focus/gradient handling is approximate. Shade-shape and shade-title are rendered approximately. "Background" fill (9) is not implemented.
15. **Shadows** other than simple offset are not emulated (`TODO`).
16. **Picture crop** (0x0100–0x0103) is not read, and neither are picture transparency colour, greyscale/washout mode and contrast (read but not applied).
17. **WordArt / text effects** (gtext properties, text-path shape types 136–175) are not implemented. The shapes would draw as their fallback geometry with no text.
18. **Only 3 of 10 adjust values** are read.
19. BLIP mapping by BStore position rather than `foDelay`. Fixed 44-byte FBSE stride.
20. **Line compound styles**, joins, some arrowheads and per-side dash styles are not read.

**Quill**

21. **STSH**: the meaning of the first STSH chunk is unknown. The `FIXME` asks whether STSH2 maps FDPP style indices to STSH1. Style names are not read.
22. **`textSize2`, `bold2`, `italic2`**: meaning unknown (`FIXME`s). bold2 and italic2 are seen only in 2007+ files. Bold and italic are taken from presence alone, which may be wrong when a run explicitly turns them *off*.
23. The **STRS header** (first two DWORDs) is guessed, per libmspub's own comment. SYID's first DWORD is unknown. The quill chunk-entry fields (the leading u16 and the u32 "normally 1") are unknown, and there is a `FIXME` asking what to do when the u16 is not 0x18. `name2` is unused.
24. **Lists**: the sub-value read bug (§4.5). The numbering delimiter is not read. There is no multi-level list or list indent support.
25. **Tabs**: only positions are read. Alignment and leaders are not.
26. **Table cells**: the formatting sub-ids 0x09–0x0E are a `TODO`. Text that ends mid-paragraph in a cell is not handled.
27. **Hyperlinks, fields** (page number, date, merge fields), **inline objects** in text, and **footnotes** do not exist in libmspub.
28. **Embedded font EOT length** and BorderArt image length may be over-read by 4 bytes (a `TODO` about reading the data as part of the block).
29. **Document chunk parse** always returns success (`FIXME: return false for failure`).
30. **Run offsets** truncated to 16 bits (a latent bug for large text).
31. **2k line flip bits** are guessed (`FIXME: this is a guess`). The 2k shape-type table conflicts with another researcher's values (`FIXME: Valek found different values`).

**Not covered by libmspub at all**

- OLE objects (equations, embedded Excel)
- hyperlinks
- web publications
- mail-merge data sources
- print-publication settings: bleeds, crop marks, colour separations, spot inks
- baseline guides and ruler guides
- layers
- "Design Checker" data
- VBA
- fonts that are not embedded, beyond the name

---

## 8. Earlier versions (brief)

### 8.1 Publisher 2000 (`E8 AC 22 00` + Quill present) — `MSPUBParser2k`

Contents:

- The trailer offset is at **0x16** (u32).
- The trailer has `u16 numBlocks`, then `numBlocks × {u16 ?, u16 id (= seqnum), u16 parentId, u32 offset}`.
- The **chunk type is the u16 at the chunk start**:

| u16 at chunk start | Chunk |
|---|---|
| 0x14 | page |
| 0x15 | document |
| 0x02 | image shape |
| 0x21 | image data (WMF) |
| 0x0F | group |
| 0x47 | palette |
| 0x00, 0x04–0x08 | shapes: 0x04 line, 0x05 rectangle, 0x06 preset shape (subtype u8 at +0x31), 0x07 ellipse, **0x08 text box** |

- **Hierarchy is by parentId.** A shape's parent is its page or group. There is no Escher and no page shape lists.
- Master: seqnum 0x109. Dummy pages: 0x108, 0x10B, 0x10D, 0x116, 0x119.

Fixed-offset shape record:

| Offset | Size | Meaning |
|---|---|---|
| +4 | u16 | counter-rotation in tenths of a degree (rotation = 360 − v/10). Ignored for groups and lines. |
| +6 | 4 × s32 | xs, ys, xe, ye, EMU (same page-centre origin) |
| +0x2A | u8 | fill type (2 = solid) |
| +0x22 | u32 | fill colour |
| +0x2C | u8 width + u32 colour | the single or left line |
| +0x35 | three × {u8 width, u32 colour}, separated by one byte each | top, right and bottom lines (rectangles only) |
| +0x58 | u16 | text id (text boxes) |
| +0x33 (preset shape) / +0x41 (line) | u8 | flip flags: bit 0 flipV; bits 1 or 4 flipH (a guess) |

Other 2k details:

- Line width byte: 0x81 means none; values above 0x81 use a special scale; otherwise the value is ×4 **quarter-points**.
- Document chunk: +0x14 width u32, height u32 (EMU).
- Palette chunk: +0xA0, 8 × u32 colours.
- 2k colour refs:
  - high byte 0xC0/0xE0 = user palette index
  - 0x00/0x80 = index into a fixed 56-entry table (black, white, red, green, blue, yellow, cyan, magenta, greys, …)
  - 0x20/0x90 = RGB
- Only solid fills; gradients and patterns are not implemented.
- Text uses the same Quill format as 2k2. Quill colour entries are translated into 2k2-style references on the fly.

### 8.2 Publisher 97/98 (`E8 AC 22 00`, no Quill) — `MSPUBParser97`

The structure is the same as 2k, with different offsets:

| Field | Offset |
|---|---|
| text id | +0x46 |
| text box marker | 0x0000 |
| lines | +0x22 / +0x2D |
| fill type | +0x20 |
| fill colour | +0x18 |

Other differences:

- Document chunk: +0x12 u16 (7 means a **banner** publication), then width and height.
- **Coordinates are offset by 25 in (120 in for banners)** from the page centre origin.
- **Text lives in `Contents`**, not Quill. It is 8-bit in a code page detected heuristically.
  - Paragraphs end with CR LF, shapes are separated by 0x0C, and 0x0B is a line break.
  - Character formatting comes from 512-byte FKP pages: the count is at +0x1FF, the run ends follow, then style indices, then variable-length property records.
  - Property record fields:
    - byte 0: bit 0 bold, bit 1 italic
    - +2: font index
    - +4: size as a signed delta in **half-points from 10 pt**
    - +8: bit 0 underline
    - +0xC: colour

---

## 9. Implementation guidance

1. Build the reader in the same stages: Quill stories keyed by text id; Contents chunks keyed by seqnum; Escher shapes linked by ClientData 0x6801; pages from `PAGE_SHAPES` and the document page list.
2. Write the block reader to be strict: unknown types are errors carrying their offset, and every container is checked against its bounds. It is the foundation for all of Contents and Quill.
3. Keep **all** blocks, known and unknown, in a raw tree per chunk. Round-trip, diffing and later reverse engineering depend on it, and these files are where the gaps in §7 live.
4. Handle rotation swapping (45–135°, 225–315°), group child-anchor mapping and the page-centre origin from day one. All three are easy to get subtly wrong.
5. Use MS-ODRAW as the authority for Escher records, FOPT semantics, BLIP headers and path commands. Use libmspub only for Publisher-specific wrapping (ClientAnchor/ClientData layout, the 4-byte container tails, seqnum links).

---

## 10. Pubsmith findings (verified against Publisher, 2026-09-28 and 2026-09-30)

Established by differential tests (diff corpus: 142 single-change variants built by Publisher, each with
Publisher's own layout export as ground truth) and by the 36 real files. These **override** the libmspub-based
notes above where they differ.

| Topic | Finding | Evidence |
|---|---|---|
| Block types | `0x00` and `0x02` are zero-length flags (in addition to 0x05/0x08/0x0A/0x78). Any other unknown type is a hard error in our reader (never assume 0 bytes). | `39 00 3c 20 …` (Contents, 365 files); `03 02 0c 22 …` (Quill, 2012 files) |
| Content pages | Document page list = `[masters…][content pages…][4 internal pages]`. Take the non-master entries of the **raw** list minus the last four (an internal entry is sometimes not a PAGE chunk at all). libmspub's hard-coded dummy seqnums (0x10D/0x110/0x113/0x117) are wrong for 2012 files; page block 0x06 and document block 0x23 are **not** a page type/count. | 35/36 real files match Publisher's page count and size |
| Anchor | Zero-valued edges are **omitted** from the 0xF010 record; absent = 0. | `geometry/left-306`, `top-396` |
| Patriarch | The first SpContainer of a drawing's root SpgrContainer is the patriarch (FSP flag 0x4); its members are the top-level shapes, not a group. | every file |
| TertiaryFOPT | Reuses property ids with different meanings (`0x01FF` = 0x400000 while the primary 0x01FF says line off). Keep it separate from the primary/secondary set. | `line/no-line` |
| Line visibility | Primary 0x01FF with use-bit 19 → bit 3 decides (0x80000 off, 0x80008 on). If the use-bit is clear (older files), the per-side border flag TertiaryFOPT **0x057F bit 19** decides (0x280020 bordered, 0x200020 not). Otherwise on. | `picture/base` vs `picture/border`; an older real file |
| Alignment | Paragraph block 0x04: only the **low byte** is the alignment; real files store e.g. 0x8002 (= centre). | real files |
| Crop to shape | Contents shape block 0xB7 = 3 → picture masked to an ellipse. | a real label's circular picture |
| All caps | Quill 0x14 is display-only; the stored text keeps its case (Publisher's export returns the typed text). | `text/allcaps` |
| WordArt bold / italic | Geometry-text booleans 0x00FF: bit 5 bold, bit 4 italic, each with its use bit 16 higher (MS-ODRAW). Publisher writes 0xFFFF5700 for plain WordArt and 0xFFFF5720 for WordArt created bold. | feature corpus F18-F19 (created bold) vs `wordart/*` (not bold); italic from the spec only |
| Group rotation | A group's rotation turns its members about the group's centre, on top of their own rotation. Members are laid out in the group's unrotated frame (stored swapped at 45–135° / 225–315°, like any shape). | `group/rotated` vs Publisher's exported frame (same centre); real files' rotated groups |
| Empty paragraphs | An empty paragraph is as tall as its paragraph mark's character formatting (the run covering its U+000D). | a real poem page: Publisher's stanza gap is 2.0 line pitches |
| Space after, last paragraph | Publisher keeps no space-after on a text box's last paragraph (set through the object model, it reads back 0), so bottom anchoring never includes it. | probe files built and read back through Publisher |
| Deleted shapes | FSP flag 0x8 (MS-ODRAW fDeleted) is not drawn. | spec; not seen in 335 files |
| BorderArt | Contents shape block 0x09 (libmspub's BorderArt index) appears in none of 335 files, so it is not read. | absence in 335 files |
