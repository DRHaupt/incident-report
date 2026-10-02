# Incident Report Manager

A native Windows desktop application (C# / WinForms, .NET 8) for recording, tracking and printing incident reports. Reports are stored in a local SQLite database, so no server is required.

## Features

- **45 report forms in 12 categories.** General, Health & Safety, Medical, Security, Vehicle & Fleet, Environmental, Property & Facilities, Information Technology, Human Resources, Healthcare & Care, Education & Childcare, and Hospitality & Retail.
- **Standard sections on every form.** Each form has incident information, reporter details, a narrative, witnesses, follow-up and corrective action, and reviewer sign-off. Each form type adds its own sections on top of these.
- **Workflow.** A report moves through Draft → Submitted → Under Review → Closed. A report can't be submitted until its required fields (marked `*`) are filled in. Closed reports are read-only until someone reopens them.
- **Attachments.** Photos, statements and other documents are stored inside the database with the report.
- **Audit history.** Every create, save, status change and attachment change is recorded with the user and a timestamp.
- **Report list.** Filter by status, incident date and category. Search covers every field. Click a column header to sort, and select a report to see it in the preview pane.
- **Printing.** Reports print as paginated forms with header and footer. You can also print a blank form to fill in by hand.
- **Export.** Export a single report as HTML or CSV, and the report list as CSV.
- **Options.** Set the organization name (printed on reports), the default reporter, department and location, the report-number prefix, and the database location. Point the database at a network share to use it from several PCs.
- **Database backup.** Uses SQLite's online backup, so you can back up while the app is open.

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

`Start(...)` adds the standard Incident Information and Reporter sections. `CommonSections.Footer` adds the Description, Witnesses, Follow-Up and Sign-Off sections. Reports store their values by field key, so you can add fields to an existing form without breaking reports saved earlier.
