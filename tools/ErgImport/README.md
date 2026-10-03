# ErgImport – building the hazmat dataset from the official ERG

The Field Toolkit's Hazmat Lookup reads `Content/erg/erg.json`. The repository ships the **ERG2024** dataset,
converted with this tool from `ERG2024-Eng-Web-a.pdf` (in the repository root) and verified in
[docs/erg/ERG2024-verification.md](../../docs/erg/ERG2024-verification.md). Use these steps for a future edition. Nothing is typed in from memory: every
material, guide line and distance comes from the PDF text and keeps its source page number.

## 1. The file you need

**The English Emergency Response Guidebook PDF, published by the U.S. DOT Pipeline and Hazardous Materials
Safety Administration (PHMSA).**

* Where: <https://www.phmsa.dot.gov/hazmat/erg/emergency-response-guidebook-erg>. Download the English
  "ERG 2024" PDF. Its file name is something like `ERG2024-Eng-Web-a.pdf`.
* Use the full guidebook PDF, not the mobile app, a scanned copy or a translated edition. The parser needs
  the PDF's text layer. It has been run on the ERG2024 English PDF; a new edition may change the layout, so
  check the import log and the cross-check.
* The ERG is a U.S. Government work and is in the public domain.

## 2. Placard → initial guide table

The ERG's *Table of Markings, Labels and Placards and Initial Response Guide to Use On-scene* is printed as
images, so text extraction can't read it. `placards.csv` holds the ERG2024 values, transcribed from printed
pages 8–9. Check them against a new edition before importing it:

```
code,guide
3,<guide number printed next to the FLAMMABLE placard>
...
```

If you leave it empty, the placard picker still filters materials by hazard class wherever the dataset
has class information.

## 3. Convert

From the repository root (requires the .NET 8 SDK):

```powershell
dotnet run --project tools/ErgImport -c Release -- pdf ERG2024-Eng-Web-a.pdf --out erg.json --dump-text erg-pages
```

The tool:

1. extracts every page's text (full-width lines and column-by-column lines for the two-column lists),
2. classifies the pages: yellow ID list, blue name list, orange guides, green Table 1, Table 2 and Table 3,
3. parses materials (ID, guide, `P` polymerization flag, name, wrapped names), guides (title, sections,
   subsections, bullet items), Table 1 distances (small and large spills, day and night, "Refer to
   Table 3"), and the Table 2 water-reactive list,
4. marks highlighted materials (those in Table 1), cross-checks the yellow list against the blue list,
5. writes `erg.json` and `erg.import-log.txt` (every line it could not place, and any validation problems).

If the log shows extraction problems, you can correct the page text in `erg-pages/` by hand and re-run the
parser on it:

```powershell
dotnet run --project tools/ErgImport -c Release -- text erg-pages --source ERG2024-Eng-Web-a.pdf --out erg.json
```

## 4. Verify (required before use)

```powershell
dotnet run --project tools/ErgImport -c Release -- verify erg.json --sample 30
```

This runs consistency checks: guide numbers are continuous, every guide has its three sections, every
material's guide exists, every Table 1 entry matches a material, and placards have guides. It also writes
`erg.verification.md`, a checklist of randomly sampled materials, Table 1 rows and complete guides, each
with its PDF page number. Open the PDF and tick each one that matches exactly. Keep the completed checklist
with the dataset as your verification record.

## 5. Install

Copy `erg.json` to `Content\erg\erg.json` next to `FieldToolkit.exe`, or into the content folder set under
**Settings** on the launcher. Then open a new Hazmat window. The banner shows the edition and import date.
Tap the banner to see the source file's SHA-256 and any data problems.

## How the PDF is read

* Text is rebuilt from individual letters. Each page's main reading direction is turned upright first, so the
  landscape Table 1 pages read like the others. Sub/superscripts (CO₂) join their line, and the PDF's own
  space characters are kept so justified text doesn't run together.
* Wide gaps between columns are marked internally, so side-by-side blocks inside a guide ("Small Spill |
  Large Spill") are read column by column.
* In Table 1, rows that share distances are printed as a block with the distances vertically centred on
  it. The parser gives a distance line to the rows waiting above it, plus as many lines below it again of the
  same ID number. Wrapped names ("(when spilled in water)") stay with the row above, even across a page
  break.
* Guide pages ERG 2024 prints as "Intentionally left blank" (121, 167) are recorded as blank, and the boxed
  Canadian ERAP notice and "*" footnotes are stored as guide notes.

## Independent cross-check

```powershell
python tools/ErgImport/crosscheck.py ERG2024-Eng-Web-a.pdf src/FieldToolkit/Content/erg/erg.json
```

This needs Poppler's `pdftotext`. It re-reads every page with that unrelated extractor and lists any imported
text it can't find on the recorded page. For ERG2024 it reports 9 items, all reviewed and explained in the
verification report: names that wrap around a Table 1 distance line, and one footnote with a stray space in
the PDF.

## Limitations

* **Table 3 is not imported.** Table 3 gives large-spill distances for six common TIH gases by container
  type and wind speed. Affected Table 1 rows show "Refer to Table 3"; use the printed guidebook for them.
* The placard table is printed as images, so `placards.csv` is transcribed by hand from pages 8–9. Re-check
  it for each new edition.
