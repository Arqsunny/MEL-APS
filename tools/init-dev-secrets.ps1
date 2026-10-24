<#
  One-time local setup of the development secrets. Nothing here is committed:
    .env.dev                          (git-ignored)  MEL_DB_PASSWORD, used by docker compose and the test runners
    .NET user secrets (your profile)  ConnectionStrings:Mel and Auth:DevTokens:SigningKey, used by the backend in Development

    powershell -File tools/init-dev-secrets.ps1                          # generate random values
    powershell -File tools/init-dev-secrets.ps1 -DbPassword <existing>   # keep an EXISTING database volume working (its password was set when the volume was created)
    powershell -File tools/init-dev-secrets.ps1 -Force                   # regenerate the sign-in key / overwrite .env.dev

  Changing the database password does not change an existing volume's password: to rotate it, recreate the volume
  (docker compose --env-file .env.dev down -v), which deletes the local data.
#>
param([string]$DbPassword, [switch]$Force)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $root '.env.dev'
$project = Join-Path $root 'backend'

function New-Token([int]$bytes) {
  $b = New-Object byte[] $bytes
  [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
  ([Convert]::ToBase64String($b) -replace '[^A-Za-z0-9]', '')
}

if (-not $DbPassword) {
  if ((Test-Path $envFile) -and -not $Force) {
    $line = Get-Content $envFile | Where-Object { $_ -match '^MEL_DB_PASSWORD=' } | Select-Object -First 1
    if ($line) { $DbPassword = ($line -split '=', 2)[1].Trim() }
  }
  if (-not $DbPassword) { $DbPassword = (New-Token 24).Substring(0, 24) }
}
if ($DbPassword -match '[;=\s]') { throw 'The password must not contain ; = or spaces (it goes into a connection string).' }

Set-Content -Path $envFile -Value "MEL_DB_PASSWORD=$DbPassword" -Encoding ascii
Write-Host "Wrote $envFile (git-ignored)"

$conn = "Host=localhost;Port=5433;Database=mel;Username=mel;Password=$DbPassword"
dotnet user-secrets set "ConnectionStrings:Mel" $conn --project $project | Out-Null

$existing = dotnet user-secrets list --project $project | Where-Object { $_ -like 'Auth:DevTokens:SigningKey*' }
if (-not $existing -or $Force) {
  dotnet user-secrets set "Auth:DevTokens:SigningKey" (New-Token 48) --project $project | Out-Null
  Write-Host 'Generated a new random dev sign-in key (user secrets).'
} else {
  Write-Host 'Dev sign-in key already present in user secrets; kept.'
}
Write-Host 'Done. Next: docker compose --env-file .env.dev up -d'
