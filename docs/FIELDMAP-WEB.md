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
| Works with no signal | Map only (areas you've viewed or saved) | Everything |
| Satellite / hybrid base map when online (Esri World Imagery, Sentinel-2) | ✓ | ✓ |
| Imagery that works offline (imagery packages) | – | ✓ |
| Drawing, printing, APRS, radio coverage prediction, terrain | – | ✓ |

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
