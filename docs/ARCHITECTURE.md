# Architecture

This document describes how the solution is split up and how the Field Toolkit's shared services work.
The Incident Report Manager, the Field Toolkit and Field Map live in the same solution and share branding and
UI infrastructure. Field Map's design (rendering, tiles, routing, search, updates) is described in
[FIELDMAP-ARCHITECTURE.md](FIELDMAP-ARCHITECTURE.md), its data sources in [FIELDMAP-DATA-SOURCES.md](FIELDMAP-DATA-SOURCES.md).

## Projects

```
IncidentReporting.sln
├─ src/Shared                  net8.0-windows  Shared WinForms infrastructure (all apps)
│    Branding.cs               Brand palette + per-app logo/icon loading (BrandAssets)
│    UI/UiMode.cs              Standard/Touch scaling + Standard/Daylight/Night theme, applied live
│    UI/UiModeButtons.cs       Toolbar toggles and Ctrl+T / Ctrl+D / Ctrl+N handling
│    UI/Ui.cs, VStack, InputDialog, WrapLabel   Glove-friendly buttons, stacks and prompts
│    Settings/JsonSettings.cs  Load/save JSON settings in %LOCALAPPDATA%\<app>
├─ src/Shared.Core             net8.0          UI-free shared code: coordinates (decimal/DMS/DDM/UTM/MGRS),
│                                              distance & bearing, NMEA, phonetic read-out, dispatch text block,
│                                              JSON/CSV helpers, JsonListStore, window layout, session state,
│                                              Field Toolkit ↔ Field Map exchange folder
├─ src/Shared.Location         net8.0-windows10.0.19041.0  LocationService (Windows location / serial NMEA / manual)
│                                              and the position-source dialog
├─ src/IncidentReporting       net8.0-windows  Incident Report Manager
├─ src/FieldToolkit.Core       net8.0          Toolkit logic: content models, loaders, search, timers, ERG,
│                                              checklists, kit, radio reference
├─ src/FieldToolkit            net8.0-windows10.0.19041.0  The Field Toolkit app
├─ src/FieldMap.Core           net8.0          Field Map logic (see FIELDMAP-ARCHITECTURE.md)
├─ src/FieldMap.Rendering      net8.0          Skia map renderer (shared by the app and Linux render tests)
├─ src/FieldMap                net8.0-windows10.0.19041.0  The Field Map app
├─ tools/ErgImport             net8.0          Converts the official ERG PDF into erg.json + verifies it
├─ tools/MapBuilder            net8.0          Builds/updates/exports Field Map region data from the command line
├─ tests/FieldToolkit.Tests    net8.0          xUnit (Linux and Windows)
├─ tests/FieldMap.Tests        net8.0          xUnit (Linux and Windows): import, tiles, search, routing,
│                                              navigation, updates/rollback, packages, formats, rendering
└─ tests/*.UiTests             Windows         Smoke tests that open every window of each field app
```

`FieldToolkit.Core` has no Windows dependencies, so its tests run on any OS (including CI on Linux),
and the logic is kept out of the forms.

## Window manager (multi-window)

* The **Launcher** is the app's main window. It has large tiles for each tool, the arrange commands,
  the active-timer panel, and the Touch Mode / Daylight toggles.
* Each tool runs in its own top-level `ToolWindow` (own taskbar entry, resizable, movable, no owner).
  Several windows of the same tool can be open at once.
* Tools are registered in `ToolRegistry` as `ToolDescriptor { Id, Title, Description, Icon, Factory }`.
  To add a tool you write a `ToolWindow` subclass and add one registry line. The launcher tiles,
  session restore and arrange commands pick it up automatically.
* Each `ToolWindow` reports a small **state string** (for example the open topic and step) so the
  session can be restored.
* `WindowManager` tracks the open tool windows and performs the **arrange** commands: side-by-side,
  stacked, 2×2 grid and cascade. It uses the working area of the screen the launcher is on.
  The rectangle maths is in `FieldToolkit.Core/Windows/WindowLayout.cs` and is unit tested.
* **Keep on top**: every tool window has a pin button in its header (sets `TopMost`).
* **Session restore**: when the app closes, the open windows (tool, state, bounds, pinned) are saved
  to `session.json`. The next start offers "Restore N windows" on the launcher. The
  "Restore windows on start" option can be set to Ask, Always or Never.
* The app is **single instance**. Starting it again brings the existing launcher to the front.

## Timer service

* `TimerService` (Core) holds `FieldTimer` records: stopwatch (counts up) or countdown, each with a
  label, a source (for example "First Aid · Severe bleeding") and optional reminder thresholds.
* Timers store **absolute UTC start times**, not tick counts. They keep running while their window
  is closed or minimized, and also while the app is closed. They are saved to `timers.json` after
  every change.
* The app runs one UI clock (250 ms) that calls `TimerService.Tick(now)`. That raises `Alert` events
  when a countdown expires or a reminder threshold passes. The UI shows these as a flashing banner
  and a sound.
* The launcher's **Active timers** panel and the timer strip at the bottom of every tool window show
  live timers. Tapping one opens the Timers tool.
* Tools create timers through the service: for example "Start tourniquet timer" or "Record stroke
  symptom onset" in First Aid.

## Content data formats

### First aid (`Content/firstaid/`)

* `_cpr.json`: CPR helper rate range, ratios, depths and hand positions per age group (clinical values are
  data, not code).
* `_guide.json`: disclaimer, review record (`reviewedBy`, `reviewedOn`), content version, and the
  plain-language **search synonyms** ("fitting" → seizure, "bleeding a lot" → severe bleeding…).
* One file per topic, for example `cpr-adult.json`:

```json
{
  "id": "cpr-adult", "title": "CPR – Adult", "category": "Life-threatening", "order": 10,
  "pinnedFirst": false,
  "keywords": ["not breathing", "no pulse", "cardiac arrest"],
  "summary": "…",
  "callEmergency": { "headline": "Call emergency services now", "when": ["…"] },
  "steps": [ { "title": "…", "text": "…", "action": { "type": "cpr" } },
             { "title": "…", "text": "…", "action": { "type": "timer", "label": "Tourniquet on",
                                                       "mode": "stopwatch", "remindEveryMinutes": 30 } } ],
  "warningSigns": ["…"], "doNot": ["…"], "related": ["choking-adult"],
  "sources": [ { "body": "American Heart Association", "title": "…", "year": 2025, "url": "…" } ],
  "review": { "status": "Draft – awaiting clinical review", "reviewedBy": "", "reviewedOn": "" }
}
```

The files are copied next to the executable (`Content\firstaid`). A reviewer can edit them in any
text editor. The app re-validates them on start and lists any problems on the About/Content screen.
You can also set a content folder outside the install directory so edited content survives
upgrades.

### Hazmat (`Content/erg/erg.json`)

`erg.json` holds `edition`, `isSample`, `guides[]` (number, title, sections → subsections → bullet
items), `materials[]` (id, name, guide, polymerizes, `tih` flag, optional hazard class),
`isolationTable[]` (Table 1 small/large spill, day/night distances) and `placards[]`.
`tools/ErgImport` produces this file from the official PHMSA ERG PDF. The app shows a red **SAMPLE
DATA** banner on every screen while `isSample` is true.

## Theming

`UiMode` has two independent switches, and both apply live to every open window:

| Switch | Values | Effect |
|---|---|---|
| Scale  | Standard / **Touch** (Ctrl+T) | Fonts, rows, buttons, menus, docked chrome grow ×1.3 |
| Theme  | Standard / **Daylight** (Ctrl+D) / **Night** (Ctrl+N) | Daylight: pure white surfaces, black text, heavy borders, semibold text. Night: near-black surfaces, dim warm text, dimmed signal colours (keeps night vision) |

Daylight and Night work as **colour transforms**: every control keeps the colours it was given for the
standard theme. In Daylight they are mapped as follows: dark colours become black, light and pastel
colours become white, and strong signal colours (red, green, amber, blue) become their most saturated
form. Text is set to black or white to contrast with the result. Tool code therefore uses one palette
(`Shared.Brand`). Colours that change at run time go through `UiMode.SetColors`, and custom-painted
controls use `UiMode.Map`.
The Field Toolkit's tool windows use large buttons in both modes. Touch Mode makes them larger still
for use with gloves.

## User data

Tool data the user creates is kept in `%LOCALAPPDATA%\FieldToolkit\` as one JSON file per list:
saved points, checklist templates and runs, kit items, channel plan, call signs and the radio log. Each file
is a `JsonListStore<T>` (Core), written atomically after every change. If a file can't be read, it is copied
aside as `*.bad` rather than overwritten. Reference content that an organisation may review or adapt lives
in `Content\` next to the program, like the first-aid and hazmat data: `checklists\templates.json`
(starter checklists, copied once into the user's data) and `radio\reference.json`.

## Location sources

`LocationService` (app) gives one current `GeoFix` to every Location window and to the radio log's
"+ My location" button. It runs only while a Location window is open. The source is chosen in settings:

* **Windows** – `Windows.Devices.Geolocation.Geolocator` (built-in GNSS, Wi-Fi). It asks for access and
  explains how to enable location for desktop apps if access is off.
* **Serial** – a background reader on a COM port. It feeds `NmeaReader` (Core), which takes GGA and RMC
  sentences from any talker and checks their checksums.
* **Manual** – a typed position, always labelled as manual.

Coordinate formatting, parsing (decimal, DMS, DDM, MGRS), UTM/MGRS conversion, and distance and bearing are
all in Core and unit tested against reference values.
