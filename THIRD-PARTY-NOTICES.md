# Third-party notices

Pubsmith is MIT-licensed (see `LICENSE`). It uses the following packages, unmodified, from NuGet; each keeps its
own licence.

| Package | Licence | Used by |
|---|---|---|
| [SkiaSharp](https://github.com/mono/SkiaSharp) | MIT | `Pubsmith.Rendering` (drawing, PNG and PDF output) |
| [OpenMcdf](https://github.com/ironfede/openmcdf) | MPL-2.0 | `Pubsmith.PubReader` (reading the OLE compound-file container) |
| [xUnit](https://github.com/xunit/xunit) | Apache-2.0 | tests |
| [coverlet](https://github.com/coverlet-coverage/coverlet) | MIT | tests (coverage) |
| [Microsoft.NET.Test.Sdk](https://github.com/microsoft/vstest) | MIT | tests |

OpenMcdf is used as a binary package under MPL-2.0; no OpenMcdf source is included in or modified by this
repository.

## Test files

- Produced by Microsoft Publisher from scripts in `tools/oracle`, and included only as test inputs and expected
  results: the `.pub` files in `tests/fixtures/pub/`, Publisher's layout exports of them (`tests/fixtures/pub/truth/`,
  and the documents and pictures in `tests/fixtures/imported/`) and its renders (`tests/fixtures/pub/golden/`,
  `tests/fixtures/golden/`). Besides our own test content they carry Publisher's own default data (for example its
  font table, form-template strings and, in the WordArt files, a built-in fill texture) and text drawn in Microsoft
  fonts; that material is Microsoft's and is not licensed under this repository's MIT licence.
- `tests/fixtures/libreoffice/` holds LibreOffice's render of one of those test files (for the comparer's tests).
- `tests/fixtures/assets/sample-picture.png` and `sample-strip.png` are drawn by `tools/art/make-sample-art.py`
  (procedural shapes) and are MIT-licensed with the rest of the repository.

## File-format knowledge

- **MS-ODRAW** (Office Drawing Binary File Format) and **MS-CFB** (Compound File Binary Format) are Microsoft
  Open Specifications.
- **libmspub** (LibreOffice, MPL-2.0) was studied to write `docs/format/pub-format-notes.md`. Those notes are our
  own description of the format, not a copy of libmspub code, and libmspub code is not included in Pubsmith.
  Section 10 of the notes records where our own differential tests against Publisher disagree with libmspub.

Microsoft and Publisher are trademarks of Microsoft Corporation. Pubsmith is not affiliated with or endorsed by
Microsoft.
