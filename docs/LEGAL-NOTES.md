# Legal and policy notes for review

These notes list the points a lawyer, privacy officer or the organisation's management should look at before the
apps are used in operations. They are a checklist written by the developers, **not legal advice**.

## 1. Disclaimers shown in the apps

| App | Text (where shown) | To check |
|---|---|---|
| Field Map | "Field Map supports, and does not replace, official CAD and dispatch systems. Map data may be incomplete or out of date: confirm locations, road status and facility details with dispatch." (launcher, About, exported maps) | Wording, and whether staff must acknowledge it |
| Field Map – AEDs | "AED locations may be out of date or inaccessible. Confirm with dispatch." (every AED, Nearest AEDs) | Fine as is, or stronger |
| Field Toolkit – First Aid | "This guide supports first aid training. It does not replace it…" plus a review record per topic | **Clinical sign-off is required before use** (`reviewedBy`/`reviewedOn` are empty) |
| Field Toolkit – Hazmat | ERG text exactly as printed, with edition and import date | Whether the ERG's own "how to use" and disclaimer pages should also be shown |
| Incident Report Manager | None | Whether reports need a confidentiality notice on screen and in print |

## 2. OpenStreetMap (ODbL 1.0)

* **Attribution** "© OpenStreetMap contributors" is shown on the map, in About and on every exported/printed map.
  ✓ Done.
* **Produced works** (map images, PNG/PDF exports, printouts) may be shared under any terms with the attribution.
* **Derived databases**: the routing graph, search index and vector tiles Field Map builds are derived databases.
  ODbL share-alike applies if a derived database is **publicly used** (made available to the public, or
  produced works made from it are shown to the public). Using the data inside the organisation is generally
  understood not to be public use; giving the built data or the app with data to other organisations or the
  public may be. If that happens, the derived database (or a way to recreate it) must be offered under the ODbL.
  The MapBuilder tool and its inputs are that way to recreate it. *Confirm with counsel for your situation.*

## 3. Open Government Licences (Canada, British Columbia, Alberta)

Attribution statements are shown with each installed layer and on exports. The licences require that use doesn't
suggest endorsement by the government; check that nothing (logos, wording) does.

## 4. Third-party data you install

* **Imagery**: only sources whose terms allow the use. Every online source ships disabled; the user accepts the
  terms before first use. See `docs/FIELDMAP-DATA-SOURCES.md#imagery`.
* **AED lists**: the Heart Safe and BC PAD listings have no open licence. A snapshot of the public websites was
  compiled on 3 October 2026. **Ask both programs for permission or an official export** before relying on it
  operationally, and keep a record of their answer.
* **Event map files** (KML/KMZ/GPX…): used as provided by the event; no redistribution.

## 5. APRS and amateur radio

* Receiving needs no licence. **Transmitting** (beacons, messages, acknowledgements) needs an amateur radio
  licence (ISED Basic Qualification) and the operator's own call sign. The app only transmits with a valid call
  sign set.
* ISED's amateur rules (RBR-4) **limit using amateur radio for the business of an organisation or for pay**.
  Decide whether staff may transmit as part of their job, and say so in policy. Receiving APRS is not affected.
* APRS is public: positions sent are visible on public websites such as aprs.fi.
* APRS-IS usage follows aprs-is.net: filtered port, software identified in the login, verified passcode to send.
* Software identifier (tocall): `APZFMP` (experimental) until a permanent one is allocated – see
  `docs/APRS-TOCALL-REQUEST.md`.

## 5a. Radio coverage and repeater data

* **Predicted coverage is an estimate** (terrain only, 64 m, one main obstacle per path). The app says so next to
  every prediction. Decide whether crews may plan with it and how it is checked on the ground.
* **Cell coverage maps** are carrier-reported and unverified; shown with that warning. Confirm the licence shown
  with the CRTC / Atlas of Canada download before sharing the imported layer outside the organisation.
* **RepeaterBook** data: their terms allow personal, non-commercial use by registered users and forbid
  redistribution; API access requires an approved token. The app only imports a file a user downloaded with their
  own account and does not share it. Using it for an organisation's operations may count as commercial use –
  ask RepeaterBook, or use your own repeater list. Don't put RepeaterBook files in USB packages or shared folders.
* **ISED spectrum data**: check the terms on ISED's download page. Some public-safety stations may be left out of
  the public data on purpose; an absent repeater doesn't mean there is none.
* **Elevation** (NRCan CDEM): Open Government Licence – Canada; attribution shown in Layers.

## 6. Privacy and records (incident reports, drawings, messages)

* Incident reports can hold personal and health information. Depending on the organisation, **BC FIPPA / PIPA** or
  **Alberta FOIP / PIPA** apply: collection notice, retention schedule, access requests, breach response.
* Data is stored **on the laptop, unencrypted** by the apps: incident database, attachments, drawings, APRS
  messages and passcode, Field Toolkit radio log. **Turn on BitLocker** (or equivalent) on every Toughbook, use
  Windows accounts with passwords, and decide where backups go.
* The Incident Report Manager database can be pointed at a network share; check where that share is hosted and
  who can read it.
* Field Map does not send anything anywhere except: map-data and live-feed downloads (when online), imagery tiles
  (if enabled) and APRS (if enabled).

## 7. Software licences

All third-party components are listed with their licences in `installer/licenses/THIRD-PARTY-NOTICES.txt`, which
the installer copies next to the programs. All are permissive (MIT, Apache 2.0, BSD, public domain). The
installer is built with the WiX Toolset **v5** (Microsoft Reciprocal Licence; the reciprocal terms cover changes to
WiX itself, not the installers it builds). WiX v6 and later require an Open Source Maintenance Fee from
organisations with more than US$10,000 revenue, so the build is pinned to v5; revisit before upgrading.

## 8. Updates

* The apps download updates only from the places configured under *Check for Field Suite updates* (by default the
  repository's GitHub releases). Nothing installs without a person approving it (and Windows asking for an
  administrator), except IT's `--unattended` command.
* **Release signing key** (`UPDATE_SIGNING_KEY` secret): whoever holds it can produce updates every installed copy
  accepts. Decide who may hold it, keep it only in the secret store, and replace it if it may have leaked.
* **GitHub access tokens** on laptops (needed while the repository is private) are encrypted per Windows user. Use
  fine-grained, read-only tokens limited to this repository with an expiry date; revoke a token if a laptop is lost.

## 9. Not yet decided

* Who owns the code and the built data; under what licence the apps are given to others (if at all).
* Code-signing certificate holder (whose name appears in the Windows install prompt).
