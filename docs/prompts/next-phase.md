# Prompt for the next phase (new chat)

Copy everything below the line into a new Claude Code session on this repository.

---

You're continuing work on **DRHaupt/incident-report**, branch `claude/windows-incident-reporting-app-0x4xva` (this is the only branch; there is no `main` yet). It holds three native Windows desktop apps for emergency response at events and in the field in **British Columbia and Alberta**, used on rugged **Panasonic/Getac Toughbook** laptops with **gloves, in direct sunlight, at night, and often with no network**:

* **Incident Report Manager** (`src/IncidentReporting`) – 96 incident report forms, SQLite, workflow, attachments, printing.
* **Field Toolkit** (`src/FieldToolkit`, `src/FieldToolkit.Core`) – launcher + tool windows: First Aid, CPR, Hazmat (ERG 2024), Location (GPS, MGRS), Timers, Checklists, Kit, Radio.
* **Field Map** (`src/FieldMap`, `src/FieldMap.Core`, `src/FieldMap.Rendering`) – offline map (OSM vector tiles, SkiaSharp), search, nearest-by-drive-time, routing with turn restrictions, incident drawings, imagery, event map-file layers (KML/KMZ/GPX…), APRS (APRS-IS + KISS TNC, messaging, SmartBeaconing), AED registry import.

Read first: `README.md`, `docs/ARCHITECTURE.md`, `docs/FIELDMAP-ARCHITECTURE.md`, `docs/LEGAL-NOTES.md`, and skim `src/Shared`, `src/Shared.Core`, `src/Shared.Location`, `src/FieldToolkit/Framework` (ToolRegistry, ToolWindow, WindowManager), `src/FieldMap/AppServices.cs`, `src/Shared.Core/Interop/Exchange.cs`, `installer/Package.wxs` and `.github/workflows/build.yml`.

## What to build (in this order)

### 1. Sharing between laptops ("Field Suite Sync") – first, because everything else depends on it
Every laptop is an island today: drawings, saved points, incident reports, APRS messages and (soon) the unit board and patient list stay on the device that made them. Design and build a sync layer shared by all apps:

* **Transports**: (a) local network (event Wi-Fi, a vehicle router or a phone hotspot) with automatic discovery of other Field Suite devices; (b) **USB stick** "sync packages" for when there is no network, both directions; (c) optionally a folder on a network share.
* **Works fully offline and peer-to-peer**: no cloud service, no internet, no central server required (a laptop can act as the hub for an event).
* **Security**: devices join an **event/team** with a shared key or QR-style join code; all traffic and USB packages **encrypted and authenticated**; a device can be removed; nothing syncs with unknown devices. Patient data (item 3) must be protected end to end.
* **Data model**: records with stable IDs, per-record version/clock and device ID, tombstones for deletes, **conflict handling** that never silently loses data (keep both / show the conflict for human choice where it matters, e.g. incident reports), and an audit trail of who changed what.
* **What syncs**: Field Map drawings and event layers, Field Toolkit saved points, checklist runs and radio log, Incident Report Manager reports (respecting its workflow and audit history), the Event Command board and the Patient Tracker. Each app chooses what it shares; the user can see sync status, last sync time per device and pending changes.
* Put the protocol and storage in a UI-free shared library with thorough tests that run on Linux (two or three simulated devices, partitions, conflicts, replays, tampered packages).

### 2. Event Command (new app) – ICS-style unit board for the event or medical desk
* **Units/crews**: status (Available, Assigned, En route, On scene, Transporting, At hospital, Out of service) with times, crew members and radio call signs, capabilities (ALS/BLS, 4x4, side-by-side…).
* **Incidents/tasks**: create, assign units, track progress, close; link to an Incident Report Manager report and to a Field Map location/drawing.
* **Personnel accountability (PAR)**: check-in/check-out, roll calls, **overdue timers** with alerts (reuse the Field Toolkit timer model).
* **Event log** (time-stamped, who wrote it) and **ICS forms**: ICS 201 (incident briefing), ICS 214 (activity log), ICS 205 (radio plan, from the Field Toolkit channel plan) – printable and exportable.
* **Map**: units placed from APRS positions (Field Map's APRS service) or manual updates; open the unit or incident in Field Map. Big touch-friendly board view for a desk monitor plus a compact view for a vehicle.
* Syncs through item 1 so the desk and the crews see the same board.

### 3. Patient Tracker (new app, or a module of Event Command – propose which)
* **Live list of patients** on site: triage category (START/JumpSTART), chief complaint, location, status (treating, awaiting transport, transported – with destination and unit, discharged, refused), times.
* **Vitals over time** with reminders when the next set is due; GCS, pain, SpO₂, BP, pulse, resp, temp, glucose; trends shown simply.
* **Treatments and medications** given (who/when), with the Field Toolkit first aid content as reference only.
* **Handover sheet** (MIST / SAMPLE / ATMIST) for paramedics: printable and copyable; one tap to create the matching **Incident Report Manager** report with details filled in.
* **Mass-casualty mode**: fast triage entry, counts by category, tag numbers.
* **Privacy is a hard requirement**: health information under BC FIPPA/PIPA and Alberta FOIP/PIPA/HIA as applicable – encryption at rest (not just BitLocker), access by user role, audit log of every view/change, retention and secure purge, export only on purpose. Write down what you implement and what the organisation still has to decide (update `docs/LEGAL-NOTES.md`).

### 4. Smaller additions to Field Toolkit (new tools in its launcher)
* **Contacts directory**: hospitals (ED phone), dispatch, police/fire non-emergency, poison centres (BC DPIC, Alberta PADIS – verify numbers), event staff, agencies; phone/radio/notes; offline, editable, importable from CSV, syncable.
* **Rally / motorsport stage log**: radio points, stage open/close, cars through with times and numbers, incidents at a point; links to the motorsport report forms in Incident Report Manager.
* **Crew fatigue / shift hours**: on-duty time per person, breaks, alerts at configurable limits.

## How to work (same rules as before)

* **Propose the design first** (a `docs/` design note per item: data model, sync protocol, screens, security), then build in the order above. Ask me only where a decision is genuinely mine.
* **Tech**: C# / WinForms / .NET 8, UI built in code (no designer files), reuse `src/Shared` (branding, UiMode: Touch Ctrl+T, Daylight Ctrl+D, Night Ctrl+N applied live to every window), `Shared.Core` (JSON stores, coordinates, CSV, dispatch text, exchange folder) and the launcher/ToolWindow/WindowManager pattern. New apps get a launcher with big tiles, session restore, arrange, and a logo designed to match the family (`assets/`, `tools/render-logos.py`).
* **Glove/sunlight/night friendly**: big targets, high contrast, everything usable in all three themes; works with no network.
* **Before choosing any third-party library or protocol, state its licence and whether it works offline.** Prefer our own code or permissive licences (MIT/Apache/BSD). No cloud dependencies.
* **No invented data** (phone numbers, guidelines, medication doses); mark anything unverified.
* **Tests**: UI-free logic in `.Core` projects with xUnit tests that run on Linux; a Windows UI smoke test per app in CI (see `tests/FieldMap.UiTests`) that opens every window and switches every theme. Keep **Windows CI green**; if a test fails, find the root cause (no skipping, no "flaky" without proof).
* **Installer**: add every new app as a feature in `installer/Package.wxs` (WiX **v5** only – v6+ needs a paid maintenance fee), with shortcuts; the CI install test must cover it. Update the quick-reference cards (`docs/quick-reference/`) and the README.
* **Commit and push as you go** with clear messages. Another Claude session sometimes pushes to the same branch: fetch and **merge** (don't rebase or force-push) before pushing. Don't open a pull request unless I ask.
* Keep personal or third-party data files I upload (e.g. AED lists) **out of the repository** unless I say otherwise.
* My amateur radio call sign is **VA7DHR** (only for docs/registrations; never as an app default).

Start by reading the code and docs above, then give me the design for item 1 (sync) together with a short outline of items 2–4 so I can confirm the direction.
