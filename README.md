<p align="center"><img src="assets/logo-512.png" width="128" alt="Incident Report Manager logo"></p>

# Incident Report Manager

A native Windows desktop application (C# / WinForms, .NET 8) for recording, tracking and printing incident reports. Reports are stored in a local SQLite database, so no server is required.

## Features

- **96 report forms in 17 categories** (see the table below), including a full set of stage rally / motorsport event forms.
- **Standard sections on every form.** Each form has incident information, reporter details, a narrative, witnesses, follow-up and corrective action, and reviewer sign-off. Each form type adds its own sections on top of these.
- **Workflow.** A report moves through Draft → Submitted → Under Review → Closed. A report can't be submitted until its required fields (marked `*`) are filled in. Closed reports are read-only until someone reopens them.
- **Linked reports.** Follow-up forms (investigations, corrective actions, witness statements, medical intervention, stewards referrals and others) link back to the original report. Use **Create Follow-Up Report** on any report to start one with the incident details already copied in. Each report has a **Linked Reports** page showing its parent and follow-ups.
- **Attachments.** Photos, statements and other documents are stored inside the database with the report.
- **Audit history.** Every create, save, status change and attachment change is recorded with the user and a timestamp.
- **Report list.** Filter by status, incident date and category. Search covers every field. Click a column header to sort, and select a report to see it in the preview pane.
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

```powershell
dotnet build IncidentReporting.sln -c Release
dotnet run --project src/IncidentReporting
```

Or open `IncidentReporting.sln` in Visual Studio and press F5.

### Single-file executable

```powershell
dotnet publish src/IncidentReporting/IncidentReporting.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

This produces `publish\IncidentReporting.exe`, which runs without .NET installed. The GitHub Actions workflow in `.github/workflows/build.yml` builds the same executable on every push and uploads it as a build artifact.

## Data location

| Item     | Default path                                           |
|----------|--------------------------------------------------------|
| Database | `%LOCALAPPDATA%\IncidentReportManager\incidents.db`    |
| Settings | `%LOCALAPPDATA%\IncidentReportManager\settings.json`   |

## Project layout

```
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

## Logo

The source artwork is `assets/logo.svg`. After you edit it, regenerate `Resources/app.ico` (16 to 256 px) and `Resources/logo-48.png` / `logo-128.png` from the SVG. The app embeds these files, and they are used for the window and taskbar icon, the report header, the About box, the preview pane, printouts and HTML exports.
