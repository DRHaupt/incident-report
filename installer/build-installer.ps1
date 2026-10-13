# Builds the Field Suite MSI on a Windows PC with the .NET 8 SDK (same steps as the CI workflow).
#   powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 [-Version 0.1.0]
param([string]$Version = "0.1.0")
$ErrorActionPreference = "Stop"
# Building needs the .NET 8 SDK (and internet access for the first build). Installing the apps does not:
# download the ready-made MSI from the repository's Releases page instead.
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "The .NET 8 SDK isn't installed, so the installer can't be built on this PC." -ForegroundColor Yellow
    Write-Host "To just install the apps, download the latest FieldSuite-x.y.z-x64.msi from the repository's Releases page instead."
    Write-Host "To build here: winget install Microsoft.DotNet.SDK.8  (then open a new PowerShell window and run this again)."
    exit 1
}
$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root "publish"
$common = @("-c", "Release", "-r", "win-x64", "--self-contained", "true", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:Version=$Version")
dotnet publish "$root\src\IncidentReporting\IncidentReporting.csproj" @common -o "$publish\IncidentReportManager"
dotnet publish "$root\src\FieldToolkit\FieldToolkit.csproj" @common -o "$publish\FieldToolkit"
dotnet publish "$root\src\FieldMap\FieldMap.csproj" @common -o "$publish\FieldMap"
dotnet publish "$root\tools\MapBuilder\MapBuilder.csproj" @common -o "$publish\MapBuilder"
# Debug symbols aren't installed.
Get-ChildItem $publish -Recurse -Filter *.pdb | Remove-Item
dotnet build "$root\installer\FieldSuite.wixproj" -c Release "-p:SuiteVersion=$Version" "-p:PublishRoot=$publish\"
# The update manifest next to the MSI: copy both into a FieldSuite-Updates folder on a USB stick to update laptops
# from the apps (signed only if UPDATE_SIGNING_KEY is set in this PowerShell session).
$msi = Get-ChildItem "$root\installer\bin" -Recurse -Filter "FieldSuite-$Version-x64.msi" | Select-Object -First 1
$release = Join-Path $root "release"
New-Item -ItemType Directory -Force $release | Out-Null
Copy-Item $msi.FullName $release -Force
dotnet run --project "$root\tools\UpdateTool\UpdateTool.csproj" -c Release -- manifest (Join-Path $release $msi.Name) $Version $release
Get-ChildItem $release | Select-Object FullName, Length
