<p align="center"><img src="assets/logo-512.png" width="112" alt="Incident Report Manager logo">&nbsp;&nbsp;&nbsp;&nbsp;<img src="assets/fieldtoolkit/toolkit-512.png" width="112" alt="Field Toolkit logo"></p>

# Incident Report Manager & Field Toolkit

This repository holds two native Windows desktop applications (C# / WinForms, .NET 8). Both work fully
offline and store all their data locally:

* **[Incident Report Manager](#incident-report-manager)** records, tracks and prints incident reports.
* **[Field Toolkit](#field-toolkit)** is a multi-window toolkit for rugged in-vehicle laptops: first aid
  guide, CPR helper, hazmat lookup and timers.

Both apps share branding, the switchable **Touch Mode** (Ctrl+T) and the high-contrast **Daylight** theme
(Ctrl+D) through the `src/Shared` library. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the design.

---

# Field Toolkit

<p><img src="src/FieldToolkit/Resources/firstaid-128.png" width="48" alt=""> <img src="src/FieldToolkit/Resources/cpr-128.png" width="48" alt=""> <img src="src/FieldToolkit/Resources/hazmat-128.png" width="48" alt=""> <img src="src/FieldToolkit/Resources/timers-128.png" width="48" alt=""></p>

The Field Toolkit is built for touchscreen Toughbook-class laptops. It is designed for gloves, direct
sunlight and no network connection, for use at events and in general emergency response work.

## Windows and layout

* **Launcher** with large tiles for each tool, the list of active timers, the open windows (tap to jump to
  one), and the Touch Mode, Daylight and Settings buttons.
* Every tool opens in its **own top-level window**, with its own taskbar entry, resizable and movable.
  You can open several windows of the same tool: **+ Window** in a First Aid or Hazmat window opens
  another one.
* **Arrange** bar on the launcher: *Side by side*, *Stacked*, *Grid* (2×2, or more for more windows),
  *Cascade*, *Minimize all*, *Close all*. Side-by-side and stacked switch to a grid when windows would get
  too small for a 10–14" screen.
* **Pin on top** button in every window header.
* **Session restore**: open windows, their positions, pin state and contents (e.g. the topic and step you
  were on) are saved on exit. On the next start you can restore them. Under Settings choose *Ask*,
  *Always* or *Never*.
* **Timers keep running** when their window is closed or minimized, and even while the app is not running,
  because they store absolute start times. Running timers appear on the launcher and in a strip at the
  bottom of every tool window. An alert flashes the taskbar, plays a sound (optional) and turns the timer
  red until someone taps it.
* **Touch Mode** and **Daylight** apply live to every open window. Daylight uses pure black and white,
  strong signal colours, heavy borders and semibold text.
* The app is single instance: launching it again brings the running launcher to the front.

## Tools

### First Aid & Emergency Guide

* 26 topics: scene safety & primary survey (DRABC / ABCDE, always listed first); CPR for adults, children
  and infants; choking (adult/child and infant); severe bleeding & tourniquet; shock; burns; fractures &
  sprains; head injury; suspected spinal injury; seizures; stroke (FAST); heart attack; anaphylaxis;
  asthma; diabetic emergency; heat exhaustion/stroke; hypothermia; drowning; electric shock;
  poisoning/overdose; bites & stings; unconscious casualty / recovery position; emergency childbirth.
* Each topic has a red **"call emergency services"** banner (tap it to see when to call), **one step per
  screen with large Back/Next buttons**, an **All steps** view, **Warnings · Do NOT**, and **Related ·
  Sources** (related topics can open in a new window).
* **Plain-word symptom search**, for example "not breathing", "bleeding a lot", "fitting", "can't
  breathe", "face drooping" or "low sugar". You can edit the synonym list.
* **Step actions**: *Start tourniquet timer*, *Record symptom onset time* (stroke; can be back-dated),
  *Start seizure timer* (alerts at 5 min), burn cooling and cold-pack countdowns, *Open CPR helper*.
  Timers you start here go into the shared timer system.
* A disclaimer and the content review status ("reviewed by / on") are shown on the home page. Tap them to
  see the review status of every topic and any content problems.

### CPR Helper

On-screen metronome (100–120/min), an optional click sound, a compression counter, 30:2 / 15:2 / hands-only
modes with **GIVE 2 BREATHS** prompts, and a 2-minute "swap compressors / follow AED prompts" reminder.
There are Adult, Child and Infant presets. Starting the helper records **"CPR started"** in the shared
timers. Press Space to start or stop.

### Hazmat Lookup (ERG)

* Search by 4-digit UN/NA number (there is an on-screen **keypad** for gloves; "UN1203", "NA 1993" and
  partial numbers also work), by material name, or by guide ("guide 128").
* Each material page shows the ID, guide and flags (P = may polymerize, water-reactive), then the **full
  guide page**: potential hazards, public safety (protective clothing, evacuation) and emergency response
  (fire, spill or leak, first aid).
* Materials in the **Table of Initial Isolation and Protective Action Distances** are highlighted in green.
  Their page shows small and large spill distances, with isolation distance and day and night protective
  action distances.
* **Placard picker**: tap a hazard class placard to filter materials and show its initial response guide.
* ⚠️ **The repository ships a SAMPLE dataset only.** It contains six fictitious materials (IDs 9901–9906,
  guides 901–903, all marked SAMPLE), and the app shows a red banner while it is loaded. Build the real
  dataset from the official PHMSA ERG PDF with [`tools/ErgImport`](tools/ErgImport/README.md).

### Timers

Start stopwatches and countdowns (presets from 1 to 30 min, or a custom length). You can pause, restart,
rename, delete and acknowledge them. All timers from all tools are listed here.

## Content: editing and review

Content lives in editable data files next to the executable, not in the code:

| File | Contents |
|---|---|
| `Content\firstaid\_guide.json` | Disclaimer, review record (`reviewedBy`, `reviewedOn`), emergency and poison-centre numbers shown in the text, search synonyms |
| `Content\firstaid\<topic>.json` | One topic: when to call, steps (with optional timer/CPR/topic actions), warning signs, do-NOT items, related topics, **sources**, review record |
| `Content\firstaid\_cpr.json` | CPR helper rate range, ratios, depths and hand positions per age group, with sources |
| `Content\erg\erg.json` | Hazmat dataset produced by `tools/ErgImport` |

A medical reviewer can change these files in any text editor; no recompiling is needed. The app checks
the files on start (missing sources, broken links between topics, invalid timers…) and lists any problems
under *About this content*. To keep reviewed content safe across program updates, copy the `Content`
folder somewhere else and select it under **Settings → Content folder**.

> **Clinical content status.** The first-aid and CPR content was drafted from published guidelines (2025
> AHA CPR & ECC Guidelines, 2024 AHA / American Red Cross First Aid Guidelines, ERC Guidelines 2025, ILCOR
> CoSTR, and the American Red Cross First Aid/CPR/AED manual for electric shock and childbirth). Every
> topic records its sources, and where the bodies differ (for example burn cooling time, initial rescue
> breaths for children, aspirin dose) the text says so. **It has not been reviewed by a clinician.** Every
> topic is marked *Draft – awaiting clinical review* until a qualified reviewer fills in `reviewedBy` /
> `reviewedOn`.

## Data location

| Item | Path |
|---|---|
| Settings, running timers, last session | `%LOCALAPPDATA%\FieldToolkit\` (`settings.json`, `timers.json`, `session.json`) |
| Content | `Content\` next to `FieldToolkit.exe`, or the folder chosen under Settings |

## Adding a tool

The planned tools (location/GPS, checklists, kit/expiry tracker, radio & comms reference) fit into the same
framework:

1. Create `src/FieldToolkit/Tools/<Name>/<Name>Window.cs` deriving from `ToolWindow`. Put the UI in
   `Body`, and override `SaveState()` and read the state in the constructor so session restore works.
2. Add logo artwork `assets/fieldtoolkit/<name>.svg` and run `python tools/render-logos.py`.
3. Add one `ToolDescriptor` to `Framework/ToolRegistry.cs`.

The launcher tile, multiple windows, arrange, pin, session restore, the timer strip and Touch/Daylight
styling all work without further code. Timers go through `AppServices.Timers`. Put UI-free logic in
`FieldToolkit.Core` so it can be unit tested.

## Keyboard

| Key | Action |
|---|---|
| Ctrl+T / Ctrl+D | Touch Mode / Daylight theme (any window) |
| Ctrl+L | Bring the launcher forward |
| ← / → (PgUp / PgDn) | Previous / next first-aid step |
| Esc | Back to the topic list / search |
| Space | Start/stop the CPR metronome |

---

# Incident Report Manager

A native Windows desktop application for recording, tracking and printing incident reports. Reports are
stored in a local SQLite database, so no server is needed.

## Features

- **96 report forms in 17 categories** (see the table below), including a full set of stage rally / motorsport event forms.
- **Standard sections on every form.** Each form has incident information, reporter details, a narrative, witnesses, follow-up and corrective action, and reviewer sign-off. Each form type adds its own sections on top of these.
- **Workflow.** A report moves through Draft → Submitted → Under Review → Closed. A report can't be submitted until its required fields (marked `*`) are filled in. Closed reports are read-only until someone reopens them.
- **Linked reports.** Follow-up forms (investigations, corrective actions, witness statements, medical intervention, stewards referrals and others) link back to the original report. Use **Create Follow-Up Report** on any report to start one with the incident details already copied in. Each report has a **Linked Reports** page showing its parent and follow-ups.
- **Attachments.** Photos, statements and other documents are stored inside the database with the report.
- **Audit history.** Every create, save, status change and attachment change is recorded with the user and a timestamp.
- **Report list.** Filter by status, incident date and category. Search covers every field. Click a column header to sort, and select a report to see it in the preview pane.
- **Daylight theme.** **View > Daylight** or **Ctrl+D** (or the toolbar button) switches to a high-contrast palette for direct sunlight, live in every window.
- **Touch Mode.** Use the **Touch Mode** button at the right of the toolbar, **View > Touch Mode** or **Ctrl+T** to switch to a touch-friendly layout. It uses larger text, taller list rows, bigger buttons and menus, wider input fields, and turns multi-select options into large toggle buttons. The switch applies immediately to every open window, and the app remembers your choice. You can also change it under **Tools > Options**.
- **Printing.** Reports print as paginated forms with header and footer. You can also print a blank form to fill in by hand.
- **Export.** Export a single report as HTML or CSV, and the report list as CSV.
- **Options.** Set the organization name (printed on reports), the default reporter, department and location, the report-number prefix, and the database location. Point the database at a network share to use it from several PCs.
- **Database backup.** Uses SQLite's online backup, so you can back up while the app is open.

## Form catalog

| Category | Forms |
|---|---|
| General | General Incident, Near Miss, Hazard / Unsafe Condition, Witness Statement, Complaint / Grievance, Lost & Found |
| Health & Safety | Employee Injury / Illness, Slip Trip & Fall, Equipment / Machinery, Hazardous Material Exposure, Fire, Ergonomic, Electrical Shock / Arc Flash, Lockout / Tagout Violation, Heat Stress, Serious Injury Regulatory Notification |
| Medical | Medical Emergency, First Aid Log, Bloodborne Pathogen / Needlestick, Communicable Disease Exposure, Animal Bite |
| Security | Security Incident, Theft / Loss, Vandalism, Trespass / Ban Notice, Use of Force, Missing Person, Threat / Bomb Threat, Lost Key / Access Card, Active Threat / Lockdown, Parking Lot / Garage, Emergency Drill Evaluation, Security Daily Activity Log |
| Vehicle & Fleet | Vehicle Accident, Forklift / Powered Industrial Truck, Post-Accident Drug & Alcohol Testing, Passenger Incident, Cargo Damage / Shipping Loss |
| Environmental | Spill / Release, Air Emission / Odor Complaint, Severe Weather Damage |
| Property & Facilities | Property Damage, Utility Outage, Water Leak / Flood, Indoor Air Quality / Mold, Elevator / Escalator |
| Information Technology | Cybersecurity Incident, Data Privacy Breach, IT Service Outage |
| Human Resources | Workplace Violence, Harassment / Discrimination, Policy Violation, Reasonable Suspicion Drug / Alcohol, Fitness for Duty / Return to Work, Workplace Bullying |
| Healthcare & Care | Patient / Resident Safety Event, Medication Error, Pressure Injury, Restraint / Seclusion Use, Adverse Drug Reaction, Infection Control Breach |
| Education & Childcare | Student / Child Injury, Behavioral Incident, Student Bullying, School Bus Incident, Field Trip Incident |
| Hospitality & Retail | Customer / Visitor Injury, Food Safety, Cash Discrepancy / Fraud, Aquatic / Pool, Alcohol Service, Guest Room, Crowd / Event |
| Construction | Fall From Height / Scaffold, Crane / Lifting, Excavation / Trench, Underground Utility Strike, Confined Space, Hot Work, Contractor / Subcontractor |
| Investigations & Follow-Up | Incident Investigation / Root Cause Analysis, Corrective & Preventive Action (CAPA) |
| Safeguarding | Suspected Child Abuse / Neglect, Suspected Elder / Vulnerable Adult Abuse |
| Manufacturing & Quality | Product Defect / Quality Non-Conformance |
| Motorsport & Rally | Stage Incident (Car Off / Accident), Rally Medical Intervention, Spectator Safety, Red Flag / Stage Stoppage, Road Closure Breach, Service Park / Refuel Zone, Marshal / Official Incident, Landowner Property Damage Claim, Competitor Conduct / Stewards Referral, Scrutineering Non-Compliance, Rally Vehicle Recovery Log |

## Requirements

- Windows 10 or 11
- To build: the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), or Visual Studio 2022 with the ".NET desktop development" workload

## Build and run

The solution `IncidentReporting.sln` contains both apps.

```powershell
dotnet build IncidentReporting.sln -c Release
dotnet run --project src/IncidentReporting      # Incident Report Manager
dotnet run --project src/FieldToolkit           # Field Toolkit
dotnet test tests/FieldToolkit.Tests            # content, search, timers, layout, ERG parser (any OS)
dotnet test tests/FieldToolkit.UiTests          # UI smoke test: opens every window (Windows only)
```

Or open `IncidentReporting.sln` in Visual Studio and press F5.

### Single-file executable

```powershell
dotnet publish src/IncidentReporting/IncidentReporting.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

This produces `publish\IncidentReporting.exe`, which runs without .NET installed. The same command with `src/FieldToolkit/FieldToolkit.csproj` produces `FieldToolkit.exe`, with its `Content` folder next to it. The GitHub Actions workflow in `.github/workflows/build.yml` builds both apps, runs both test projects on every push, and uploads both executables as build artifacts.

## Data location

| Item     | Default path                                           |
|----------|--------------------------------------------------------|
| Database | `%LOCALAPPDATA%\IncidentReportManager\incidents.db`    |
| Settings | `%LOCALAPPDATA%\IncidentReportManager\settings.json`   |

## Project layout

```
src/Shared/               Brand palette, BrandAssets, UiMode (Touch + Daylight), JSON settings (both apps)
src/FieldToolkit.Core/    UI-free Field Toolkit logic: first-aid content/search, CPR metronome, timers,
                          window layout, ERG model/parser/search
src/FieldToolkit/         Field Toolkit app: Launcher/, Framework/ (ToolWindow, WindowManager, registry),
                          Tools/ (FirstAid, Cpr, Hazmat, Timers), UI/, Content/, Resources/
tools/ErgImport/          ERG PDF → erg.json converter and verifier
tools/render-logos.py     Renders assets/fieldtoolkit/*.svg into the app's PNG/ICO resources
tests/FieldToolkit.Tests/ xUnit tests (net8.0)
tests/FieldToolkit.UiTests/ Windows UI smoke test
src/IncidentReporting/
  Program.cs              Application entry point
  Branding.cs             Product name, colors and embedded logo/icon
  Resources/              app.ico and logo PNGs (generated from assets/logo.svg)
  Models/                 Form template model and report entity
  Templates/              Form catalog (one file per category group)
  Data/                   SQLite repository and settings
  Services/               Printing, HTML/CSV export, formatting/validation
  UI/                     Main window, report editor, dialogs
```

## Adding or changing forms

Forms are declared in code under `Templates/`. To add a form, put a new entry in the relevant `Catalog.*.cs` file:

```csharp
list.Add(Start("GEN-160", "Key Control Report", "General", "Lost, issued or unreturned keys.")
    .Section("Key Details")
    .Text("key_number", "Key Number", required: true)
    .Choice("key_event", "Event", true, "Lost", "Issued", "Returned", "Not Returned")
    .YesNo("rekey_required", "Re-keying Required?")
    .Include(CommonSections.Footer)
    .Build());
```

Use `.Related()` to add a "Related Report" section that links the form to an earlier report.

`Start(...)` adds the standard Incident Information and Reporter sections. `CommonSections.Footer` adds the Description, Witnesses, Follow-Up and Sign-Off sections. Reports store their values by field key, so you can add fields to an existing form without breaking reports saved earlier.

## Logos

**Field Toolkit:** the source artwork is in `assets/fieldtoolkit/`. `toolkit.svg` is the app icon: the
same navy shield as the incident app, with a grid of tool tiles and an amber "+" badge. There is one icon
per tool: `firstaid.svg` (white cross on safety green), `cpr.svg` (heart with pulse line), `hazmat.svg`
(placard diamond with UN plate) and `timers.svg` (stopwatch). Each tool window and taskbar entry uses its
tool's icon. Run `python tools/render-logos.py` to regenerate the embedded PNG/ICO files.

**Incident Report Manager:** the source artwork is `assets/logo.svg`. After you edit it, regenerate `Resources/app.ico` (16 to 256 px) and `Resources/logo-48.png` / `logo-128.png` from the SVG. The app embeds these files, and they are used for the window and taskbar icon, the report header, the About box, the preview pane, printouts and HTML exports.
