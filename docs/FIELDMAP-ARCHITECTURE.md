# Field Map – Architecture

Field Map is the suite's third desktop app: an offline, CAD-style map for emergency response in British Columbia
and Alberta, built for touchscreen Toughbooks (gloves, direct sunlight, night driving, no network).

This document is the design proposal (one recommendation per decision, with the alternatives considered) and the
reference for how the app is built. The data sources and their licences are listed in
[FIELDMAP-DATA-SOURCES.md](FIELDMAP-DATA-SOURCES.md).

> **Disclaimer.** Field Map supports, and does not replace, official CAD/dispatch systems. Map data may be
> incomplete or out of date.

---

## 1. Third-party components (licence and offline use)

Everything below works with **no network** on Windows 10/11 x64 once installed. Every component is shipped inside the
self-contained build; nothing is downloaded at run time.

| Component | Used for | Licence | Offline on Windows |
|---|---|---|---|
| .NET 8 / WinForms | App runtime and UI (same as the other two apps) | MIT | Yes (self-contained publish) |
| **SkiaSharp 3.119** + SkiaSharp.Views.WindowsForms | Map drawing, PNG and PDF snapshot export | MIT (Skia itself: BSD-3-Clause) | Yes, native `libSkiaSharp.dll` is bundled |
| **Microsoft.Data.Sqlite 8** (SQLitePCLRaw `e_sqlite3`) | MBTiles tile store, search index (FTS5 + trigram), R*Tree spatial index | MIT; SQLitePCLRaw Apache-2.0; SQLite public domain | Yes, native `e_sqlite3.dll` is bundled |
| System.Speech | Spoken turn instructions (Windows SAPI voices installed with Windows) | MIT | Yes, SAPI voices are local |
| System.IO.Ports | Serial (COM port) NMEA GPS (already used by Field Toolkit) | MIT | Yes |
| Windows.Devices.Geolocation (Windows SDK projection) | Built-in GNSS (already used by Field Toolkit) | Part of Windows / .NET SDK (MIT projections) | Yes |
| xUnit | Tests | Apache-2.0 | n/a |

Everything else (OSM PBF reader, vector-tile encoder/decoder, tile builder, router, geocoder, update manager,
KML/GeoJSON/GPX) is our own code in `FieldMap.Core`, so there is nothing else to license and it all runs on Linux CI.

Considered and **not** chosen (licences checked on NuGet / upstream):

| Option | Licence | Why not |
|---|---|---|
| Mapsui 5 (WinForms control) | MIT | Good raster/vector-feature control, but its vector-tile support is immature, and our three live-switchable themes, glove gestures and CAD symbols would mean fighting its style system. |
| GMap.NET | Custom permissive (see its wiki) | Raster-only and built around online tile providers whose terms forbid bulk offline caching. |
| MapLibre GL JS in WebView2 | BSD-3 (MapLibre); WebView2 runtime is Microsoft freeware | Best-looking vector rendering, but adds a browser runtime (must be the Evergreen/fixed runtime installed offline), a JS↔C# bridge for every CAD feature, and GPU-dependent rendering on older Toughbooks. Kept as the fallback if our renderer proves too slow. |
| OsmSharp | MIT | Fine, but allocates an object per OSM element; our 400-line streaming PBF reader is faster and has no dependencies. |
| Itinero 1.x | Apache-2.0 | Embedded .NET router; no maintained release for .NET 8, truck restrictions need custom profiles, and runtime road closures are awkward with its contracted graph. |
| OSRM | BSD-2 | Very fast (contraction hierarchies) but closures need a graph rebuild, truck limits need Lua profiles, Windows builds are unofficial. |
| Valhalla | MIT | Best truck model and dynamic avoid-polygons, but a C++ process with no official Windows binaries; we could not build or test it here. Good upgrade path if we outgrow our router. |
| GraphHopper | Apache-2.0 | Good truck support, but needs a bundled Java runtime (~60 MB) and a second process. |
| tilemaker / planetiler | FTWPL / Apache-2.0 | Excellent tile generators, but they would be a C++ exe or a Java runtime on the vehicle for "Update map data", and we could not test them on Linux CI. |

---

## 2. Decisions

### 2.1 Map rendering control → **own `MapView` control drawn with SkiaSharp** (recommended)

* A WinForms control (`SKControl`) that draws vector tiles itself. We own every pixel, so the Standard (dark CAD),
  Daylight (white, black, heavy lines) and Night (very dark, dim red/amber) map styles switch live with the rest of
  the UI, and incident/unit symbols, range rings and route lines are drawn in the same pass.
* **Gestures:** Windows touch gestures (`WM_GESTURE`): pan, pinch-zoom, two-finger rotate, two-finger tap (zoom out),
  press-and-hold (= "What's here?"). Mouse: drag, wheel zoom, right-drag rotate. Large on-screen **+ / − /
  centre-on-me / north-up** buttons for gloves.
* **Speed:** decoded tiles are cached as Skia paths per layer (LRU); a frame re-draws cached paths with one matrix
  transform, so pan/zoom/rotate stay smooth on integrated graphics. Labels are re-placed (collision boxes) when a
  gesture ends. Zoom beyond the deepest tile level (14) over-zooms that tile.
* Scale bar, north arrow (tap = north up), GPS marker with accuracy circle, breadcrumb trail.

### 2.2 Offline tile format → **MBTiles containing Mapbox Vector Tiles (MVT), zoom 0–14** (recommended)

* MBTiles is one SQLite file per region (`tiles.mbtiles`), read with the SQLite library we already ship.
  MVT is the open standard (protobuf); QGIS and other tools can open our files for checking.
* PMTiles (single file, HTTP-range friendly) has no advantage offline and would need another reader.
* **Generation:** our `RegionBuilder` (in `FieldMap.Core`, also exposed as `tools/MapBuilder`) streams the Geofabrik
  `.osm.pbf` twice (ways first to find the nodes needed, then node coordinates), keeps only what the map needs
  (roads, rail, water, land use/parks/forest outline, boundaries, places, POIs; **no buildings**, which make up much
  of the BC extract), simplifies geometry per zoom (Douglas–Peucker), clips it to tile bounds and writes gzip'd
  MVT. Layers: `water`, `landuse`, `roads` (class, ref, name, bridge/tunnel, seasonal), `rail`, `boundaries`,
  `places`, `pois` (layer id, name). Tile schema is documented in the code (`TileSchema`).
* **Terrain/hillshade:** feasible from NRCan's MRDEM-30 (OGL-Canada) as a raster MBTiles overlay; not built in this
  first version (it adds ~1–2 GB per province). The style and layer toggle are ready for it.

### 2.3 Offline routing → **embedded .NET router in `FieldMap.Core`** (recommended)

| | Embedded .NET (chosen) | Bundled OSRM / Valhalla process |
|---|---|---|
| Size (BC) | ~150–250 MB graph | OSRM ~1–2 GB CH files; Valhalla ~500 MB tiles + exe |
| Query speed | A*: city ≪ 100 ms, Vancouver→Fort Nelson ≈ 1–2 s | ≪ 50 ms |
| Road closures drawn by the user | Instant (edges blocked at query time) | OSRM: rebuild; Valhalla: avoid polygons |
| Truck height/weight/length | Yes, per-edge OSM `maxheight` / `maxweight` / `maxlength` / `hgv` | Valhalla yes; OSRM via Lua |
| Nearest-N by drive time | One Dijkstra from the origin finds the N nearest by time in a single search | N separate routes or matrix API |
| Windows / CI | Pure .NET, tested on Linux CI | No official Windows build; untestable here |

* Graph: compressed sparse row (CSR) arrays in one binary file (`routing.graph`): node coordinates, edges with
  length, speed class, flags (oneway, toll, ferry, seasonal, emergency-only, bridge/tunnel) and truck limits, plus
  street names/refs for instructions. Built from the same PBF pass.
* Profiles: **Car**, **Emergency vehicle** (higher speeds on major roads, may use `access=emergency` /
  `emergency=yes` ways and gates), **Large truck/trailer** (avoids ways whose tagged height/weight/length limits are
  below the vehicle's, `hgv=no`, and residential shortcuts are penalised). Walking for AED walking times.
* A* with a time heuristic; **alternatives** by the penalty method (re-run with the best route's edges penalised,
  keep routes that differ ≥ 30 %); **re-routing** when the GPS is > 50 m off the route for 3 fixes.
* Turn-by-turn instructions from bearing changes and name/ref changes ("Turn right onto Hwy 97 N", roundabout
  exits), with distances and ETA; the route can be copied as text.
* Not in v1: turn restrictions (`type=restriction` relations) and time-dependent traffic. Documented as known gaps.

### 2.4 Geocoding and search → **SQLite FTS5 (trigram) + R*Tree index per region** (recommended)

* `search.sqlite` per region: addresses (OSM `addr:*` plus StatCan Open Database of Addresses), street segments,
  computed **intersections** (two differently named streets sharing a node), places, facilities/POIs with
  categories, highway km markers (OSM `highway=milestone` / `distance`), and AEDs.
* **Tolerant matching:** queries are normalised (case, accents, punctuation), abbreviations expanded both ways
  (St/Street, Ave, Rd, Hwy/Highway, N/North, Mt/Mount, Ft/Fort…), then matched with the FTS5 *trigram* tokenizer, and
  the candidates re-ranked by edit distance, token coverage and distance from the map centre. Typos such as
  "Kamloosp" or "Lougheed Hwy" → "Lougheed Highway" still match.
* A query parser recognises the special forms first: coordinates in any format the Field Toolkit accepts (decimal,
  DMS, DDM, MGRS, UTM), "Main St & 5th Ave" / "Main and 5th" (intersection), "Hwy 97 km 120" (km marker), "nearest
  hospital"/category words.
* **What's here?** uses the R*Tree for the nearest address point and nearest street segment/intersection.

### 2.5 Update and rollback → **versioned folders + an atomically replaced pointer file** (recommended)

```
<data folder chosen by the user, e.g. E:\FieldMapData>\
  regions\
    bc\
      current.json            ← {"version": "2026-10-01_1"}  (replaced atomically; the only thing a swap changes)
      previous.json           ← the version to roll back to
      versions\
        2026-10-01_1\         ← complete, validated, read-only once published
          manifest.json       (region, built date, per-layer source/licence/date, SHA-256 of every file)
          tiles.mbtiles  routing.graph  search.sqlite  pois.json  sources.json
        2026-09-02_1\         ← previous (kept for one-tap rollback; older versions are pruned)
    ab\ …
  downloads\                  ← resumable *.part files + *.part.json (URL, ETag, bytes, expected checksum)
  staging\                    ← builds in progress; deleted on failure
  imports\                    ← organisation AED registries (CSV/GeoJSON) re-applied after every update
```

1. **Download** each source with HTTP range requests (`.part` + metadata), resuming after interruption; verify the
   publisher's checksum (Geofabrik `.md5`) or our manifest's SHA-256. Sizes are shown before downloading (HTTP HEAD
   / catalogue).
2. **Build** into `staging\<new version>\` (tiles, routing graph, search index, POI layers).
3. **Validate**: open every file, check row/feature counts against minimums and against the previous version
   (refuse a version that lost > 30 % of roads/POIs), run sample searches and a sample route, verify checksums.
4. **Publish**: move the folder to `versions\` (same volume → atomic rename), then write `current.json.tmp` and
   `File.Replace` it over `current.json` (atomic on NTFS); the old pointer becomes `previous.json`. Open windows
   switch to the new version.
5. **Rollback**: one tap swaps `current.json` and `previous.json` the same way.
6. **Recovery**: at start-up, if `current.json` points to a missing or invalid version, the app falls back to
   `previous.json`, then to any valid version, and says so. A power cut at any point leaves either the old or the new
   version current, never a half-built one.

**Vehicles that never go online:** on a connected PC, *Data → Export update package* (or
`MapBuilder export`) writes `FieldMap-bc-2026-10-01.fmupdate` (a zip of a validated version folder + manifest with
SHA-256 for every file). On the vehicle, *Data → Install from USB* verifies every checksum, extracts to `staging\`,
validates, and publishes with the same atomic swap.

### 2.6 Storage layout and expected disk size

Source extracts (Geofabrik, checked October 2026): **British Columbia ≈ 1.2 GB**, **Alberta ≈ 330 MB** `.osm.pbf`.
Expected per installed version (estimates until the first real build; the app shows actual sizes):

| | BC | AB |
|---|---|---|
| Vector tiles z0–14, no buildings | 0.6–0.9 GB | 0.3–0.5 GB |
| Routing graph | 0.15–0.25 GB | 0.08–0.15 GB |
| Search index (OSM + ODA addresses) | 0.2–0.3 GB | 0.1–0.2 GB |
| **One version** | **≈ 1–1.5 GB** | **≈ 0.5–0.9 GB** |
| Current + previous + download cache | ≈ 4 GB | ≈ 2 GB |
| Optional hillshade (future) | +1–2 GB | +0.5–1 GB |

Building BC needs ≈ 3–4 GB RAM and 20–40 minutes on a Toughbook; building on a desktop and installing the package
from USB is quicker.

---

## 3. Projects

```
src/Shared.Core          net8.0                 Moved out of FieldToolkit.Core: coordinates (decimal/DMS/DDM/UTM/MGRS),
                                                distance & bearing, NMEA, phonetic read-out, dispatch text block,
                                                JSON helpers, JsonListStore, CSV, window layout maths, session state
src/Shared.Location      net8.0-windows10.0.19041.0  LocationService (Windows location, serial NMEA, manual) and the
                                                source dialog, used by Field Toolkit and Field Map
src/Shared               net8.0-windows         Branding, UiMode (Touch, Daylight, **Night**), glove-friendly Ui helpers
src/FieldMap.Core        net8.0                 UI-free: OSM PBF/XML reader, tile builder, MVT, MBTiles, layer catalogue,
                                                search index, geocoder, road graph & router, instructions, drawings &
                                                KML/GeoJSON/GPX, hazmat geometry, AED import, region store, updater,
                                                package export/install, live-feed parsers, Field Toolkit exchange
src/FieldMap             net8.0-windows10.0.19041.0  The app: launcher, map windows, panels, data manager
tools/MapBuilder         net8.0                 Command line: build a region, build the sample, export a package
tests/FieldMap.Tests     net8.0                 xUnit (runs on Linux): import/validation, search accuracy, routing,
                                                coordinates, update/rollback atomicity, formats
tests/FieldMap.UiTests   Windows                Smoke test: opens every window and panel, switches all themes
```

## 4. Themes

`UiMode` gains a third theme. Standard / Daylight / Night are mutually exclusive and apply live to every window,
the map included:

| Theme | UI | Map style |
|---|---|---|
| Standard | Suite navy/amber | **Dark high-contrast CAD**: near-black ground, bright roads, amber highways, white labels |
| Daylight (Ctrl+D) | Pure black/white, heavy borders | White ground, black roads, saturated signal colours, bold labels |
| Night (Ctrl+N) | Near-black surfaces, dim warm text, no white | Very dark ground, dim red/amber lines and labels, nothing bright to ruin night vision |

## 5. Field Toolkit integration

A shared folder `%LOCALAPPDATA%\FieldSuite\exchange\` holds small JSON messages:

* `to-fieldmap\*.json` – written by Field Toolkit's Location tool ("Open in map"): `{ "type": "point", "name", "lat",
  "lon", "note", "sentUtc", "from": "Field Toolkit" }`.
* `to-fieldtoolkit\*.json` – written by Field Map ("Send to Field Toolkit"); the toolkit imports them as saved points.

Each app watches its inbox (`FileSystemWatcher` plus a poll), imports a message, then deletes it. Files are written
as `*.tmp` and renamed so a reader never sees half a message. If the target app isn't running it picks the message up
on its next start; "Open in map" also starts Field Map if it is installed next to the toolkit.
