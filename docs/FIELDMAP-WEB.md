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
| Drawing, printing, APRS, radio coverage prediction, imagery, terrain | – | ✓ |

The layout adapts to the screen: a bottom sheet on a phone held upright, and a side panel next to the map on
wider screens (landscape, tablets, and foldable phones when opened). On foldables that report a hinge, the panel
sits on one half and the map on the other.

## Install on Ubuntu

You need Ubuntu 22.04 or 24.04 (64-bit Intel/AMD), a DNS name pointing at the server (for HTTPS), and ports 80 and
443 open. Phones only allow GPS on HTTPS sites.

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

3. **Add HTTPS** with Caddy, which gets and renews the certificate by itself:

   ```sh
   sudo apt install caddy
   sudo cp deploy/Caddyfile /etc/caddy/Caddyfile   # then edit the name on the first line
   sudo systemctl reload caddy
   ```

   Using nginx instead? Proxy `https://your-name/` to `http://127.0.0.1:5080` and pass `X-Forwarded-For` and
   `X-Forwarded-Proto`. Serve it at the root of its own name (e.g. `maps.example.org`), not in a sub-folder.

4. **Open it on a phone**: go to `https://your-name/`, allow location, then Share → **Add to Home Screen** (iPhone)
   or menu → **Install app** (Android).

## Settings

`/etc/fieldmap-web/appsettings.json` (restart with `sudo systemctl restart fieldmap-web` after changes):

| Setting | Meaning |
|---|---|
| `AccessCode` | When set, each phone must enter this code once. Changing it signs every phone out. Recommended if the server is on the internet, especially with event files. |
| `Regions` | Only serve these regions, e.g. `["bc"]`. Empty: every installed region. |
| `EventsDir` | Folder of shared event files. Default `/var/lib/fieldmap/events`. |
| `MaxUploadMb` | Largest event file a phone may add (it's converted and kept on that phone only). |

## Event files

- **Shared with everyone:** copy KML, KMZ, GPX, GeoJSON or CSV files into `/var/lib/fieldmap/events`. Phones see
  them on the next start (or More → Check for new data). Remove a file to take it off.
- **On one phone only:** Layers → Add a file from this phone. The server converts it and keeps nothing; the phone
  stores it.

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
