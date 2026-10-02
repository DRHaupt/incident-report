# ErgImport – building the hazmat dataset from the official ERG

The Field Toolkit's Hazmat Lookup reads `Content/erg/erg.json`. The repository ships a **synthetic sample**
(IDs 9901–9906, guides 901–903, all text marked `[SAMPLE – NOT ERG DATA]`). It exists only to exercise the app,
and the app shows a red *SAMPLE DATA* banner on every screen while it is loaded.

To load real data, convert the official guidebook with this tool. Nothing is typed in from memory: every
material, guide line and distance comes from the PDF text and keeps its source page number.

## 1. The file you need

**The English Emergency Response Guidebook PDF, published by the U.S. DOT Pipeline and Hazardous Materials
Safety Administration (PHMSA).**

* Where: <https://www.phmsa.dot.gov/hazmat/erg/emergency-response-guidebook-erg>. Download the English
  "ERG 2024" PDF. Its file name is something like `ERG2024-Eng-Web-a.pdf`.
* Use the full guidebook PDF, not the mobile app, a scanned copy or a translated edition. The parser needs
  the PDF's text layer.
* The ERG is a U.S. Government work and is in the public domain.

## 2. Optional: placard → initial guide table

The ERG's *Table of Placards and Initial Response Guide to Use On-Scene* is printed as images, so text
extraction can't read it. To enable "initial response guide" for each placard in the placard picker, fill
in `placards.csv` from that page of the guidebook:

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

## Limitations

* **Table 3 is not imported.** Table 3 gives large-spill distances for six common TIH gases by container
  type and wind speed. Affected Table 1 rows show "Refer to Table 3"; use the printed guidebook for them.
* The parser has been tested on synthetic pages that follow the ERG layout. It has **not** been run on the
  real PDF yet, because the PHMSA site could not be reached from the build environment. Expect to review
  the import log on the first real run.
