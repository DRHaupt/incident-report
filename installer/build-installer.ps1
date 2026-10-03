# Builds the Field Suite MSI on a Windows PC with the .NET 8 SDK (same steps as the CI workflow).
#   powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 [-Version 0.1.0]
param([string]$Version = "0.1.0")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root "publish"
$common = @("-c", "Release", "-r", "win-x64", "--self-contained", "true", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true")
dotnet publish "$root\src\IncidentReporting\IncidentReporting.csproj" @common -o "$publish\IncidentReportManager"
dotnet publish "$root\src\FieldToolkit\FieldToolkit.csproj" @common -o "$publish\FieldToolkit"
dotnet publish "$root\src\FieldMap\FieldMap.csproj" @common -o "$publish\FieldMap"
dotnet publish "$root\tools\MapBuilder\MapBuilder.csproj" @common -o "$publish\MapBuilder"
# Debug symbols aren't installed.
Get-ChildItem $publish -Recurse -Filter *.pdb | Remove-Item
dotnet build "$root\installer\FieldSuite.wixproj" -c Release "-p:SuiteVersion=$Version" "-p:PublishRoot=$publish\"
Get-ChildItem "$root\installer\bin" -Recurse -Filter *.msi | Select-Object FullName, Length
