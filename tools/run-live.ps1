<#
  Runs the backend in Live mode using the APS_* values from the git-ignored .env file. Secrets are never printed.

    powershell -File tools/run-live.ps1 -Command setup     # publish the AppBundle + Activity + OSS bucket (aps-setup)
    powershell -File tools/run-live.ps1 -Command run       # start the app on http://localhost:5080
    powershell -File tools/run-live.ps1 -DryRun            # show what would be used (secret masked), run nothing

  Only APS_CLIENT_ID / APS_CLIENT_SECRET are read from .env. Other keys in that file (database, JWT, ports) belong to other
  projects and are ignored. The callback URL is always this app's own unless -CallbackUrl is given: it must be registered on
  the APS app (APS portal > your app > Callback URLs).
#>
param(
  [ValidateSet('setup', 'run')][string]$Command = 'run',
  [string]$Bucket,
  [string]$CallbackUrl = 'http://localhost:5080/api/aps/callback',
  [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $root '.env'
if (-not (Test-Path $envFile)) { throw ".env not found at $envFile" }

$vals = @{}
foreach ($line in Get-Content $envFile) {
  if ($line.TrimStart().StartsWith('#')) { continue }
  if ($line -match '^\s*(?:export\s+)?([A-Za-z0-9_.]+)\s*=\s*(.*)$') { $vals[$Matches[1]] = $Matches[2].Trim().Trim('"').Trim("'") }
}
foreach ($k in 'APS_CLIENT_ID', 'APS_CLIENT_SECRET') {
  if ([string]::IsNullOrWhiteSpace($vals[$k])) { throw "$k is missing or empty in .env" }
}

# Bucket names are global across all of APS: derive a stable, unlikely-to-collide default from the client id.
if (-not $Bucket) { $Bucket = 'mel-smoke-' + $vals['APS_CLIENT_ID'].Substring(0, 8).ToLowerInvariant() }
if ($Bucket -notmatch '^[a-z0-9._-]{3,128}$') { throw "Bucket '$Bucket' must be 3-128 chars of a-z 0-9 . _ -" }

$envCallback = $vals['APS_CALLBACK_URL']
if ($envCallback -and $envCallback -ne $CallbackUrl) {
  Write-Host "Note: .env APS_CALLBACK_URL ($envCallback) is not used; this app needs $CallbackUrl registered on the APS app." -ForegroundColor Yellow
}

$env:Aps__Mode = 'Live'
$env:Aps__ClientId = $vals['APS_CLIENT_ID']
$env:Aps__ClientSecret = $vals['APS_CLIENT_SECRET']
$env:Aps__CallbackUrl = $CallbackUrl
$env:Aps__Bucket = $Bucket
# Make sure no leftover test overrides point the app at a fake server.
foreach ($n in 'Aps__AuthBaseUrl', 'Aps__DesignAutomationBaseUrl', 'Aps__OssBaseUrl', 'Aps__DataManagementBaseUrl') { Remove-Item "Env:$n" -ErrorAction SilentlyContinue }

Write-Host ("Aps__Mode=Live  Aps__ClientId={0}...({1} chars)  Aps__ClientSecret=*** ({2} chars)" -f $vals['APS_CLIENT_ID'].Substring(0, 4), $vals['APS_CLIENT_ID'].Length, $vals['APS_CLIENT_SECRET'].Length)
Write-Host "Aps__CallbackUrl=$CallbackUrl  Aps__Bucket=$Bucket"
if ($DryRun) { Write-Host 'Dry run: nothing started.'; return }

Set-Location $root
if ($Command -eq 'setup') { dotnet run --project backend -- aps-setup }
else { dotnet run --project backend --urls http://localhost:5080 }
