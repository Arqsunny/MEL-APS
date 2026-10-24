<#
  Builds the Design Automation AppBundle once per Revit release and zips each.
  Output: artifacts/MelSync.bundle.R2025.zip, artifacts/MelSync.bundle.R2026.zip
          (publish with: dotnet run --project backend -- aps-setup)
  Each bundle is compiled against that release's Revit API (installed locally, reference only, never shipped).
  A model must be opened by the engine of the SAME release (see docs/APS.md), hence one bundle per release.

    powershell -File tools/build-bundle.ps1                      # 2025 and 2026
    powershell -File tools/build-bundle.ps1 -Years 2025
#>
param(
  [int[]]$Years = @(2025, 2026),
  [string]$Version = '1.0.0'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'revit_automation\MelSync.Revit\MelSync.Revit.csproj'
$out  = Join-Path $root 'artifacts'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $out | Out-Null

foreach ($year in $Years) {
  $install = "C:\Program Files\Autodesk\Revit $year"
  if (-not (Test-Path (Join-Path $install 'RevitAPI.dll'))) { Write-Warning "Revit $year is not installed here; skipping."; continue }

  $pub = Join-Path $out "publish\$year"
  dotnet publish $proj -c Release -p:RevitYear=$year -p:RevitInstall="$install" -o $pub --nologo
  if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for Revit $year" }

  $bundle = Join-Path $out "MelSync.bundle.R$year\MelSync.bundle"
  $contents = Join-Path $bundle 'Contents'
  New-Item -ItemType Directory -Path $contents -Force | Out-Null
  # Ship only our code + the shared contracts; Revit and the DA bridge are provided by the engine.
  foreach ($f in 'MelSync.Revit.dll', 'MelSync.Core.dll') { Copy-Item (Join-Path $pub $f) $contents }

  @"
<?xml version="1.0" encoding="utf-8" standalone="no"?>
<RevitAddIns>
  <AddIn Type="DBApplication">
    <Name>MelSync</Name>
    <Assembly>.\MelSync.Revit.dll</Assembly>
    <AddInId>5b8d3c1e-6a4f-4c2b-9d7e-2f1a8b6c0e31</AddInId>
    <FullClassName>MelSync.Revit.MelSyncApp</FullClassName>
    <VendorId>MEL</VendorId>
    <VendorDescription>Master Equipment List</VendorDescription>
  </AddIn>
</RevitAddIns>
"@ | Set-Content -Encoding UTF8 (Join-Path $contents 'MelSync.addin')

  @"
<?xml version="1.0" encoding="utf-8"?>
<ApplicationPackage SchemaVersion="1.0" AutodeskProduct="Revit" Name="MelSync" Description="MEL parameter sync (Revit $year)" AppVersion="$Version" Author="MEL" ProductType="Application" AppNameSpace="MelSync" ProductCode="{c3a1f6d2-8b47-4e0a-a5d9-7e2b9f4c1d68}" UpgradeCode="{0e9d4b7a-1c53-4f68-b2a0-6d8c3e5f7a19}">
  <CompanyDetails Name="MEL" />
  <Components>
    <RuntimeRequirements OS="Win64" Platform="Revit" SeriesMin="R$year" SeriesMax="R$year" />
    <ComponentEntry AppName="MelSync" Version="$Version" ModuleName="./Contents/MelSync.addin" AppDescription="MEL parameter sync" LoadOnCommandInvocation="False" LoadOnRevitStartup="True" />
  </Components>
</ApplicationPackage>
"@ | Set-Content -Encoding UTF8 (Join-Path $bundle 'PackageContents.xml')

  $zip = Join-Path $out "MelSync.bundle.R$year.zip"
  Compress-Archive -Path $bundle -DestinationPath $zip
  Write-Host "Built $zip"
}
