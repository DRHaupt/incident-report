# Field Map – Data sources and licences

Field Map never invents data. The base map and most layers come from **OpenStreetMap**; official provincial and
federal open data adds addresses and fills gaps. Every installed layer records its **source, licence, URL and
download date**, and the app shows them in the Layers panel, on printed/exported maps, and on the About → Data screen.

**Status of this list.** The sandbox this was built in cannot reach these hosts, so nothing has been downloaded yet.
Existence, URLs and licences were checked by web search in October 2026 (marked ✓). Items marked ◻ need confirming
on first use. Until real data is installed, the app runs on a **synthetic sample region** with fictitious names and a
red **SAMPLE DATA** banner (see "Sample region" below).

## Base map and POIs

| Data | Source | Licence | Notes |
|---|---|---|---|
| Roads, rail, water, land use, places, POIs | ✓ Geofabrik extracts of OpenStreetMap: `https://download.geofabrik.de/north-america/canada/british-columbia-latest.osm.pbf` (≈ 1.2 GB) and `…/alberta-latest.osm.pbf` (≈ 330 MB), each with a `.md5` file | **ODbL 1.0** – attribution "© OpenStreetMap contributors"; derived databases must stay ODbL | Updated daily by Geofabrik |
| Addresses | ✓ Statistics Canada **Open Database of Addresses (ODA)**, per-province zipped CSV: `https://www.statcan.gc.ca/en/lode/databases/oda` | **Open Government Licence – Canada** | Combines municipal/provincial address points; coverage varies (strong in cities) |
| Health facilities (cross-check) | ✓ StatCan **Open Database of Healthcare Facilities (ODHF)**: `https://www.statcan.gc.ca/en/lode/databases/odhf` | OGL – Canada | Facility type helps mark hospitals |
| BC emergency departments | ✓ BC Data Catalogue **"Emergency Rooms in BC"** (open.canada.ca dataset `88e982b2-0799-4e17-b000-9961ea05b0f0`) | **OGL – British Columbia** | Used to flag hospitals with a 24-h ED |
| BC hospitals | ✓ BC Data Catalogue **"Hospitals in BC"** | OGL – BC | |
| BC schools | ✓ **BC Schools – K-12 with Francophone Indicators** (CSV, `catalogue.data.gov.bc.ca/dataset/95da1091-7e8c-4aa6-9c1b-5ab159ea7b42`) | OGL – BC | Has coordinates |
| BC fire, police, ambulance, coast guard | ✓ BC Data Catalogue **"First Responders"** (open.canada.ca `652c49eb-7295-4ae2-8f26-39103f23b50d`) | OGL – BC | |
| BC reception centres | ✓ **Emergency Social Services Facilities** (`catalogue.data.gov.bc.ca/dataset/8473689f-2d06-4e84-ac8b-e91033fd0f0b`) | OGL – BC | Updated as needed during events |
| AB hospitals | ✓ **Hospital services in Alberta** (`open.alberta.ca/dataset/hospital-services-in-alberta`) | **OGL – Alberta** | Addresses and types; geocode against ODA/OSM |
| AB schools | ✓ **Alberta schools and authorities** (`open.alberta.ca/opendata/alberta-education-schools-and-authorities`, monthly XLS) | OGL – Alberta | Addresses; geocoded |
| Terrain (future hillshade) | ✓ NRCan **MRDEM-30** COGs: `https://canelevation-dem.s3.ca-central-1.amazonaws.com/mrdem-30/` | OGL – Canada | Not built in v1 |
| Avalanche terrain | ✓ Avalanche Canada ATES maps (trip planner downloads) | ◻ Terms to confirm; not open-licensed as far as we found | Not included; OSM `hazard=avalanche` where tagged |

Attribution shown in the app and on every exported map:
"© OpenStreetMap contributors (ODbL) · Contains information licensed under the Open Government Licence – Canada ·
Contains information licensed under the Open Government Licence – British Columbia · Contains information licensed
under the Open Government Licence – Alberta" (only the licences of installed layers are listed).

## Layers and how they are derived

| Layer | OpenStreetMap tags | Official data merged |
|---|---|---|
| AEDs | `emergency=defibrillator` (+ `defibrillator:location`, `opening_hours`, `access`, `check_date`) | Organisation/official registry CSV or GeoJSON import (see below) |
| Hospitals (ED / other) | `amenity=hospital`, `emergency=yes/no`, `healthcare=hospital` | BC Emergency Rooms list; ODHF / AB hospital list |
| Schools | `amenity=school` (`college`/`university` separate) | BC K-12; AB schools |
| Fire / police / ambulance stations | `amenity=fire_station`, `amenity=police`, `emergency=ambulance_station` | BC First Responders |
| Helipads and airports | `aeroway=helipad`, `aeroway=heliport`, `aeroway=aerodrome` | – |
| Pharmacies | `amenity=pharmacy`, `healthcare=pharmacy` | – |
| Fuel | `amenity=fuel` (+ `fuel:diesel`, `hgv`) | – |
| Hydrants and water sources | `emergency=fire_hydrant`, `emergency=water_tank`, `emergency=suction_point`, `emergency=fire_water_pond` | – |
| Shelters and reception centres | `amenity=shelter`+`shelter_type`, `social_facility=shelter`, `emergency=assembly_point` | BC ESS facilities |
| Rest areas, washrooms, pull-outs | `highway=rest_area`, `highway=services`, `amenity=toilets`, `highway=turning_loop`/`passing_place` | – |
| Bridges, rail crossings, tunnels | `bridge=*` ways (+ `maxheight`, `maxweight`), `railway=level_crossing`/`crossing`, `tunnel=*` | – |
| Gates, seasonal and forest-service roads | `barrier=gate`/`lift_gate` (+ `access`), `seasonal=yes`, `winter_road=yes`, `highway=track`, names containing "FSR" / `operator=*Forest*` | – |
| Avalanche / rockfall areas | `hazard=avalanche`, `hazard=rockfall`, `hazard=falling_rocks` | none open found |
| Highway km markers and exits | `highway=milestone` (+ `distance`, `pk`), `highway=motorway_junction` (+ `ref`) | ◻ BC MoTI km-post data to confirm |

## AEDs

* OSM `emergency=defibrillator` points are imported with their `check_date`/`survey:date` as "last verified".
* **Import** (Data → AEDs → Import): CSV or GeoJSON. CSV columns (header names are case-insensitive): `name`,
  `latitude`, `longitude`, `address`, `access` (e.g. "24/7", "business hours"), `location` (where inside),
  `last_verified` (yyyy-mm-dd), `source` (defaults to the file name), `id`. GeoJSON: Point features with the same
  property names.
* Merge: an imported AED within 25 m of an existing one replaces its details (keeping both sources listed); others
  are added. Imports are kept under `imports\` and re-applied automatically after each map-data update.
* Every AED shows source and last-verified date, and the panel always shows: "AED locations may be out of date or
  inaccessible. Confirm with dispatch."
* Provincial registries: BC PAD program (Heart & Stroke / BCEHS, `bcpadprogram.ca`) and the Alberta PAD registry
  publish public maps but, as far as we found, **no open-licensed download**; ask them for an export and load it
  with the import.

## Live data (only when connected; nothing depends on it)

| Feed | Endpoint (checked by web search) | Terms | Notes |
|---|---|---|---|
| DriveBC road events | ✓ Open511 `https://api.open511.gov.bc.ca/events?format=json&status=ACTIVE` | ✓ Data: OGL – BC; API: BC API Terms of Use | No key |
| Alberta 511 events | ✓ `https://511.alberta.ca/api/v2/get/event?key=…&format=json` | ✓ Free developer key; 10 calls / 60 s; terms on `511.alberta.ca/developers/doc` (◻ read before release) | Key entered in Settings; feed disabled without one |
| BC Wildfire perimeters | ✓ BC Data Catalogue "BC Wildfire Fire Perimeters – Current" (WFS `openmaps.gov.bc.ca/geo/pub/wfs`, layer `WHSE_LAND_AND_NATURAL_RESOURCE.PROT_CURRENT_FIRE_POLYS_SP`, GeoJSON output) | ✓ OGL – BC; refreshed every 15 min | |
| BC evacuation orders/alerts | ✓ ArcGIS `https://services6.arcgis.com/ubm4tcTYICKBpist/arcgis/rest/services/Evacuation_Orders_and_Alerts/FeatureServer/0/query?where=1=1&outFields=*&f=geojson` | ✓ OGL – BC (EMCR) | |
| Alberta Wildfire | ✓ Alberta Geospatial Services Platform (current perimeters) – ◻ exact feature-service URL to confirm; set in Settings | ✓ OGL – Alberta | Disabled until a URL is set |
| Alberta emergency alerts | ✓ Alert Ready / NAAD CAP feed (Pelmorex) | ◻ Pelmorex NAAD terms of use must be accepted | Not enabled by default |
| APRS stations (internet) | ✓ APRS-IS volunteer network, North America rotation `noam.aprs2.net:14580` (user-defined filter port; aprs2.net) | ✓ No licence; usage rules at aprs-is.net/connecting.aspx: use port 14580, passcode -1 for read-only maps, real passcode to send. Data is what amateur stations transmit publicly | Receive-only without call sign. Also from a radio TNC with no internet (see README) |
| ECCC weather alerts | ✓ MSC GeoMet OGC API `https://api.weather.gc.ca/collections/weather-alerts/items?f=json&bbox=…` | ✓ OGL – Canada (MSC open data) | |

Each live layer shows "LIVE DATA – fetched hh:mm", is cached on disk (last good result, with its time), and fails
quietly to the cache when offline.

## How to install real data

On a PC with internet (or on the vehicle when connected):

1. Field Map → **Data** → choose the data folder (e.g. a second drive or SD card).
2. Tick **British Columbia** and/or **Alberta**. The sizes are shown; tap **Download and build**.
   The app downloads the Geofabrik extract (+ `.md5`), the ODA address CSV and the official layers listed above,
   builds tiles, routing and search, validates, and swaps them in.
3. For vehicles that never go online: **Export update package** → copy the `.fmupdate` file to USB → on the vehicle,
   **Data → Install from USB**.

Command line (same code): `MapBuilder build --region bc --pbf british-columbia-latest.osm.pbf --oda ODA_BC_v1.csv
--out <data folder>` and `MapBuilder export --region bc --data <data folder> --out E:\`.

If you prefer to download the files yourself, provide (per province): the Geofabrik `.osm.pbf` and its `.md5`, the
ODA province CSV (zip), and optionally the official CSVs listed above; then use **Data → Build from files…**.

**After the first real build:** spot-check a sample of entries against the source (the validation report lists 20
random hospitals, schools, AEDs and addresses with their source IDs for checking).

## Sample region

`FieldMap.Core/Sample/SampleRegion.cs` generates **"Sample County"**: a fictitious town grid with fictitious street
names, a highway with km markers and an exit, a bridge with a height limit, a forest-service road with a gate,
hospitals (one with an ED), schools, stations, AEDs, hydrants, a helipad, fuel, pharmacy, rest area and shelter.
It is built through exactly the same pipeline as real data (OSM XML → tiles, routing, search) and every screen shows
a red **SAMPLE DATA – fictitious, not for operational use** banner while it is installed.
