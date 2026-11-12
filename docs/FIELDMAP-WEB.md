# Field Map for phones (web version)

A phone-first web version of Field Map. It runs on your own Ubuntu server and uses the same map data, search,
nearest-by-drive-time and directions as the Windows app (built by MapBuilder). Crews open it in Safari or Chrome
and can add it to the Home Screen like an app.

## What it does

| | Phone web app | Windows Field Map |
|---|---|---|
| Map with GPS position, heading arrow, follow and heading-up | ✓ | ✓ |
| Standard, Daylight and Night map styles | ✓ | ✓ |
| Search: addresses, intersections, places, businesses, highway km, coordinates, MGRS | ✓ (on the server) | ✓ |
| Nearest hospital / fire / AED / hydrant… by drive time | ✓ (on the server) | ✓ |
| Directions (car, emergency, truck, walking) with a next-turn banner | ✓ (on the server) | ✓ |
| Facility layers (AEDs, hospitals, hydrants, gates…) | ✓ | ✓ |
| Event files (KML, KMZ, GPX, GeoJSON, CSV), event items list, search in event files | ✓ | ✓ |
| Cell coverage layer, repeaters (own, event, amateur, licensed) | ✓ (if installed on the server) | ✓ |
| Coordinates in decimal, DMS, deg-min, MGRS, UTM; copy for dispatch | ✓ | ✓ |
| Hazmat: ERG 2024 lookup (UN number, name, guide, placard), guides, isolation and protective-action zones on the map | ✓ (works offline once loaded) | Field Toolkit |
| Team positions with the last hour's trail, shared incident pins (needs an access code) | ✓ | – |
| Works with no signal | Map only (areas you've viewed or saved) | Everything |
| Satellite / hybrid base map when online (Esri World Imagery, Sentinel-2) | ✓ | ✓ |
| Imagery that works offline (imagery packages) | – | ✓ |
| APRS stations from APRS-IS (receive only), with tracks | ✓ (if turned on on the server) | ✓ |
| Live hazards: DriveBC / Alberta 511 road events, wildfire perimeters, evacuation orders and alerts, weather alerts | ✓ (fetched by the server) | ✓ |
| Drawing, printing, APRS by radio (TNC) and APRS messaging, radio coverage prediction, terrain | – | ✓ |

The layout follows the screen size only (it never checks which phone it is), and changes live when a foldable
such as the iPhone Duo is opened or closed. Search text, the open panel and the map position stay as they are.

| Screen width | Layout |
|---|---|
| about 380–480 px (closed foldable, Split View, regular iPhones) | One column: the panel slides up from the bottom |
| about 480–700 px (middle sizes, open foldable held upright) | The same sheet, inset from the edges; wider content (coordinates in two columns) |
| 700 px and up, or 560 px and up on a short screen (open foldable in landscape, phones on their side, tablets) | Two panes: panel and tabs on the left, map and status on the right |

In two panes the left pane always stops short of the middle of the screen, so no button or text sits on the
fold. The map keeps your position in the right half, and the map doesn't shift when the panel opens or closes.
Short screens (down to about 370 px high) get smaller bars so more of the map shows. Where Safari reports the fold
(the Viewport Segments and Device Posture feature flags), the panes line up exactly with the two halves, and a
half-closed "laptop" pose puts the map on top and the panel below. Without those flags everything still works.

To try it in Xcode's iPhone Duo simulator, open and close the phone while a panel is open; nothing should reload.

## Install on Ubuntu

You need Ubuntu 22.04 or 24.04 (64-bit Intel/AMD) and a way to reach it over HTTPS. Phones only allow GPS on
HTTPS sites. The usual setup is a **Cloudflare Tunnel**: no ports to open and no certificates to manage. Caddy also
works (see the end of step 3).

1. **Download** `FieldMapWeb-<version>-linux-x64.tar.gz` from the latest GitHub release and unpack it:

   ```sh
   tar xzf FieldMapWeb-*-linux-x64.tar.gz
   cd fieldmap-web
   sudo sh deploy/install.sh
   ```

   This creates a `fieldmap` system user, installs the server in `/opt/fieldmap-web`, keeps data in
   `/var/lib/fieldmap`, and starts the `fieldmap-web` service on `127.0.0.1:5080`. No .NET install is needed.
   Running it again with a newer download updates the programs and keeps your settings and data.

2. **Build the map data** (the first BC build downloads and processes a lot of data, so it takes a while):

   ```sh
   sudo -u fieldmap /opt/fieldmap-web/mapbuilder/MapBuilder update --region bc --data /var/lib/fieldmap
   ```

   Use `--region ab` for Alberta. To try things out first, `build-sample` installs the small fictitious Sample
   County in seconds. The server notices new data within a minute; no restart is needed. A weekly update runs on
   Sundays at about 03:30. Change the regions in `/etc/fieldmap-web/update.env` (for example `REGIONS="bc ab"`).

   Already have the data on a Field Map PC? Copy the `regions` folder from its data folder (Field Map's Data
   window shows which folder that is) to `/var/lib/fieldmap/regions`, then run
   `sudo chown -R fieldmap:fieldmap /var/lib/fieldmap`.
   You can also use `MapBuilder export` on the PC and `MapBuilder install --package` on the server.

3. **Connect it to the internet with a Cloudflare Tunnel.** The server only listens on `127.0.0.1:5080`, so
   nothing is exposed until the tunnel is set up.
   - Install `cloudflared` on the server and create a tunnel (Cloudflare dashboard → Zero Trust → Networks →
     Tunnels), then add a **Public Hostname** such as `maps.example.org` with service `http://localhost:5080`.
     For a tunnel managed from the command line, `deploy/cloudflared-config.yml` has the config.
   - Cloudflare provides the HTTPS. The server reads Cloudflare's headers, so each phone is known by its own
     address (wrong access codes only slow down that phone) and the sign-in cookie is marked secure.
   - In the Cloudflare dashboard for the domain: turn **Rocket Loader off** (it rewrites the app's scripts). If
     phones get "No connection to the server" while the site opens fine in a browser, check that **Bot Fight
     Mode** or a WAF rule isn't challenging `/api/` and `/tiles/` requests. Leave caching at the defaults; there's
     no need for a "cache everything" rule. With an access code set, map tiles are marked private anyway, so
     Cloudflare won't serve them to phones that aren't signed in.
   - Set an **access code** (see Settings below), since the site is on the internet. Cloudflare Access (Zero Trust
     sign-in with email codes) can go in front as well for per-person sign-in. When an Access session expires, the
     installed app shows "No connection to the server" until the site is opened again and signed in, so set a
     long session duration (e.g. a month).

   Not using Cloudflare? Caddy gets and renews a certificate by itself (ports 80 and 443 open):
   `sudo apt install caddy`, copy `deploy/Caddyfile` to `/etc/caddy/Caddyfile`, put your name on the first
   line, then `sudo systemctl reload caddy`.

4. **Open it on a phone**: go to `https://your-name/`, allow location, then Share → **Add to Home Screen** (iPhone)
   or menu → **Install app** (Android).

## Settings

`/etc/fieldmap-web/appsettings.json` (restart with `sudo systemctl restart fieldmap-web` after changes):

| Setting | Meaning |
|---|---|
| `AccessCode` | When set, each phone must enter this code once. Changing it signs every phone out. Set one whenever the server is reachable from the internet (e.g. through the tunnel), especially with event files. |
| `Regions` | Only serve these regions, e.g. `["bc"]`. Empty: every installed region. |
| `EventsDir` | Folder of shared event files. Default `/var/lib/fieldmap/events`. |
| `MaxUploadMb` | Largest event file a phone may add (it's converted and kept on that phone only). |
| `Live` | Live hazards (see below). On by default; `{ "Enabled": false }` turns them off. `Keys` holds keys for feeds that need one, e.g. `{ "Keys": { "ab511": "…" } }`. |
| `AprsIs` | APRS stations on the map (see below). `{ "Enabled": true }` turns it on; `Server`, `Port`, `Callsign`, `ExtraFilter`, `ShowMinutes` and `TrackMinutes` are optional. |

## Team positions and shared pins

Crews can see each other on the map and share pins for an incident. These features only work when an **access code**
is set (see Settings), because without one anyone who can reach the server would see where everyone is. Until then the
Team screen says that they're off.

- **Your name or callsign:** More → Team. Each phone picks the name others see (e.g. "Medic 12"). Phones are told
  apart by a random id made on the phone, so two phones can use the same name.
- **Share my position** (off until turned on): other phones see a green dot with the name and a dashed trail of the
  last hour. A dot turns grey after 2 minutes without an update and disappears after 15. Turning sharing off removes
  the position straight away. Positions are kept in the server's memory only; nothing is written to disk. A web app
  can't use GPS in the background, so positions only go out while Field Map is open on screen (More → Keep the screen
  on helps).
- **Shared pins:** touch and hold the map, then **Share as pin**, or More → Team → Add a pin here. Pick a kind
  (hazard, staging, road closed, command post, medical, note), a label and optional details. Everyone sees the pin
  within a few seconds and anyone can edit, move or remove it; each pin shows who added and last changed it.
  Pins are saved in `/var/lib/fieldmap/shared/pins.json` and kept until someone removes them; **Clear all pins** on
  the Team screen removes them all after an incident.
- Layers: More → Team → Show team on the map hides units, trails and pins on that phone.

## Live hazards

The server fetches the same live feeds as the Windows app and shows them on every phone (Layers → Live hazards, where
each kind can be turned off on that phone):

| Feed | Shows | Refresh |
|---|---|---|
| DriveBC (Open511) | Road closures, incidents and construction in BC | 5 min |
| Alberta 511 | Road events in Alberta (needs a free developer key, see below) | 5 min |
| BC Wildfire Service | Fire perimeters; fires that are out are drawn faintly | 15 min |
| BC evacuation orders and alerts | Order areas in red, alert areas in amber | 10 min |
| Environment Canada | Weather warnings, watches and statements | 10 min |

Only items inside the installed map regions are sent, with shapes simplified to about 25 m, so phones download a few
hundred kilobytes at most and only when something changed. The last good copy of each feed is kept in
`/var/lib/fieldmap/live-cache`, so phones still see it (marked with its time) while a feed or the internet is down.

- **Alberta 511 key:** request a developer key from 511 Alberta, then add it inside `"FieldMap"` in
  `/etc/fieldmap-web/appsettings.json` and restart: `"Live": { "Keys": { "ab511": "your-key" } }`.
- **Other feeds:** to change the list (for example to add the Alberta wildfire perimeters once you have its URL), copy
  `/opt/fieldmap-web/server/Content/live-feeds.json` to `/etc/fieldmap-web/live-feeds.json`, edit it and restart.
- To turn live hazards off: `"Live": { "Enabled": false }`.

## Hazmat (ERG 2024)

More → Tools → **Hazmat**, or type a UN number (e.g. `1005`) in the search box. The guidebook data is the Field
Toolkit's ERG 2024 import (62 guides, 2,771 materials, Table 1, placards); a phone downloads it once (about 200 KB
compressed) and keeps it, so lookups work without signal.

- **Search** by UN/NA number, material name or `guide 128`; with an empty box, pick a **placard** to get its initial
  response guide. Results flag toxic inhalation hazards (Table 1), water-reactive materials and polymerization (P).
- **Material:** the guide and the Table 1 initial isolation and protective-action distances (small/large spill,
  day/night). **Guide:** every section as printed, plus General First Aid.
- **Draw zones on the map:** pick the spill point (your position, the map centre or a dropped pin), small or large,
  day or night, and the direction the wind is blowing from. The isolation circle and the downwind protective-action
  zone (90° sector or the ERG square) are drawn, from Table 1 for highlighted materials and from the guide's
  EVACUATION distances otherwise. Where Table 1 says "Refer to Table 3", the app says so instead of guessing. **Share
  with team** adds a hazard pin with the distances.

### SOS and emergency alerts

- **SOS:** More → Tools → SOS (or More → Team, or tap the GPS badge). After a confirmation and an optional message,
  every team phone shows a red banner ("SOS – Medic 12 · Need medical · 1.1 km NE"), vibrates and beeps, and draws a
  large red dot at that phone's position. While the SOS is active the phone keeps sending its position (every 10 s
  with a fix), even when position sharing is off. The sender's banner has **Cancel SOS**; an SOS not updated for an
  hour ends by itself. Others can **Dismiss** it on their phone. Needs the access code, like the other team features.
- **APRS emergencies:** when the APRS layer is on and a station reports an emergency, the same banner appears.

### Send my location

Tap the GPS badge in the status bar (or More → Tools → Send my location) and choose **Send my location**: the phone's
share sheet opens with your name, coordinates and accuracy, MGRS, the nearest address, the time and Apple/Google Maps
links, ready for a text, email or any messaging app. Where there is no share sheet the text is copied.

## First aid and CPR

More → Tools → **First aid & CPR**: the Field Toolkit's first aid guide (41 topics) and CPR helper, loaded on the server
with the toolkit's own loader and checks, with the emergency, poison-centre and crisis numbers from its `_guide.json`
filled in. Phones download it once (about 220 KB) and keep it, so it works without signal.

- **Search by what's happening** ("not breathing", "fitting", "bee sting"): the toolkit's symptom search, ported so
  results match the Windows app. With an empty box, topics are listed by category, scene safety first.
- **Topics:** when to call 911, numbered steps with cautions, warning signs, what not to do, related topics and sources.
  Step buttons start **timers** (e.g. "Tourniquet applied"; kept on the phone across reloads), open the CPR helper or
  another topic.
- **CPR helper:** adult/child/infant, 30:2, 15:2 or hands-only, 100–120 per minute. A click on every beat (accented on
  the first of each cycle), the compression count, breath pauses with a countdown, and a swap-compressors reminder
  every 2 minutes. It keeps the screen on while running.
- The guide's review status is shown on every screen. **The content is marked "Draft – awaiting clinical review"**:
  have it reviewed by a qualified clinician (and update `_guide.json`) before relying on it operationally.

## APRS stations

The server can listen to **APRS-IS** (the internet side of APRS, fed by I-gates) and show the stations it hears on the
phones: other hams, digipeaters, weather stations and objects, coloured by kind, with the last hour's track of moving
stations. It is **receive-only**: the server logs in with passcode -1 and never sends anything, so no licence or passcode
is needed. It listens to the area of the installed map regions (one box per region, e.g. all of BC and Alberta) and
follows a newly installed region within a minute.

Turn it on in `/etc/fieldmap-web/appsettings.json`, inside `"FieldMap"`, then restart the service:

```json
"AprsIs": { "Enabled": true }
```

Optional settings: `Server` (default `noam.aprs2.net`), `Port` (14580), `Callsign` (shown in the server's login instead
of a generated `FMWEB####` name), `ExtraFilter` (more APRS-IS filter terms, e.g. `b/VE7*`), `ShowMinutes` (stations not
heard for longer are left off the map, default 60) and `TrackMinutes` (60). `journalctl -u fieldmap-web` shows the
connection and the filter in use. On the phones: Layers → APRS stations (on by default) shows the connection status and
turns the layer off; tap a station for its details and a link to aprs.fi.

## Event files

- **Shared with everyone:** copy KML, KMZ, GPX, GeoJSON or CSV files into `/var/lib/fieldmap/events`. Phones see
  them on the next start (or More → Check for new data). Remove a file to take it off.
- **On one phone only:** Layers → Add a file from this phone. The server converts it and keeps nothing; the phone
  stores it.

## Satellite imagery

Layers → **Base map** → *Satellite* or *Hybrid* (imagery with roads and labels) when the phone has a connection.
The server fetches the imagery for the phones, so it works through the tunnel and behind the access code. Nothing is
stored, on the server or the phone. The sources are the same as the Windows app's (`imagery-sources.json`, shipped
with the server): Esri World Imagery and Sentinel-2 cloudless. Each phone sees a source's terms once before using
it. Without signal, the map shows instead.

- To change the list, copy `/opt/fieldmap-web/server/Content/imagery-sources.json` to
  `/etc/fieldmap-web/imagery-sources.json`, edit it (`"enabled": false` hides a source), and restart the service.
- For a source that needs a key (e.g. your organisation's ArcGIS token), add it on the server only:
  `"ImageryKeys": { "esri-world-imagery-export": "<token>" }` in `appsettings.json`.

## AED lists

Copy the list (Excel, CSV, GeoJSON, KML/KMZ or GPX) to the server and run, after the region is installed:

```sh
sudo -u fieldmap /opt/fieldmap-web/mapbuilder/MapBuilder aed --data /var/lib/fieldmap --file AED_Locations.xlsx --rebuild
```

Sites with coordinates are used as they are. Sites with only a street address (such as the BC PAD Program list) are
placed with the region's offline address search and marked approximate. Sites that can't be placed (no house
number, or the address isn't in the map data) are written to `/var/lib/fieldmap/imports/aed-not-placed/` for
checking. The placed sites are merged into every later build, including the weekly update. Without `--rebuild`
they appear at the next update. Don't just copy the list into `imports/aed`: builds skip sites without
coordinates, so address-only sites would be left out.

## Cell coverage and repeaters

- Cell coverage: copy the coverage file to the server and run
  `sudo -u fieldmap /opt/fieldmap-web/mapbuilder/MapBuilder coverage --data /var/lib/fieldmap --file coverage.geojson`.
  The Atlas of Canada file works as downloaded; only the newest year of each technology is kept.
- Repeaters: copy `repeaters.json` from a Field Map PC (it's in Field Map's settings folder) to
  `/var/lib/fieldmap/repeaters.json`. RepeaterBook entries are left out on the web because RepeaterBook's terms don't
  allow passing their data on.

## Offline

Map tiles you've looked at are kept on the phone (up to about 12,000 tiles). Before going somewhere with poor
signal, open More → **Save this area** with the area on screen. Search, nearest and directions need the server.

## Checking and troubleshooting

```sh
systemctl status fieldmap-web           # is it running?
journalctl -u fieldmap-web -n 50         # recent log, including which data version is loaded
curl -s http://127.0.0.1:5080/api/status # {"ok":true,"hasData":true,...}
systemctl list-timers fieldmap-update    # when the next data update runs
journalctl -u fieldmap-update -n 100     # how the last data update went
```

- **"Location is off for this site"**: on iPhone, Settings → Privacy & Security → Location Services → Safari
  Websites → While Using. The site must be HTTPS.
- **Blank map**: the server has no data yet (step 2), or the phone can't reach it.
- **Screen keeps locking while driving**: More → Keep the screen on. On iPhone this works best from the Home Screen icon.

## How it's built

- `src/FieldMap.Web`: an ASP.NET Core server that reuses `FieldMap.Core`. It serves the region's MBTiles vector
  tiles, POIs, search, What's here, nearest-by-drive-time and routing as JSON, converts event files to GeoJSON, and
  reloads when MapBuilder installs a new version.
- `src/FieldMap.Web/wwwroot`: the phone app (plain JavaScript modules, no build step), drawn with MapLibre GL JS
  (vendored, BSD-3-Clause) using a style that follows the desktop themes. A service worker keeps the app and map
  tiles for offline use.
- Tests: `tests/FieldMap.Web.Tests` (API, against the sample region) and `node --test tests/web/coords.test.mjs`
  (coordinate formats must match the desktop app exactly).
