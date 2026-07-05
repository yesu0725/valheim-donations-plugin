# Build the plugin and deploy the DLL to BOTH destinations (client + server).
# Usage:  pwsh ./deploy.ps1        (from valheim-plugin/)
#         pwsh ./deploy.ps1 -NoBuild   (skip build, just copy the existing DLL)
param([switch]$NoBuild)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$dll  = Join-Path $here 'bin\Release\ValheimDonationSystem.dll'

$targets = @(
  'C:\Users\yesu0725\AppData\Roaming\r2modmanPlus-local\Valheim\profiles\Hearthbound Valheim - Test\BepInEx\plugins',
  'C:\Program Files (x86)\Steam\steamapps\common\Valheim dedicated server\BepInEx\plugins'
)

if (-not $NoBuild) {
  Write-Host "Building (Release)..." -ForegroundColor Cyan
  dotnet build -c Release (Join-Path $here 'ValheimDonationSystem.csproj') | Out-Host
  if ($LASTEXITCODE -ne 0) { throw "Build failed — not deploying." }
}

if (-not (Test-Path $dll)) { throw "DLL not found: $dll" }

foreach ($t in $targets) {
  $dest = Join-Path $t 'ValheimDonationSystem.dll'
  if (-not (Test-Path $t)) { Write-Host "SKIP (missing folder): $t" -ForegroundColor Yellow; continue }
  try {
    Copy-Item $dll $dest -Force
    Write-Host "deployed -> $dest" -ForegroundColor Green
  } catch {
    # Most common cause: the dedicated server is running and holds the DLL.
    Write-Host "FAILED  -> $dest" -ForegroundColor Red
    Write-Host "         ($($_.Exception.Message)) — is the dedicated server running? Stop it and re-run with -NoBuild." -ForegroundColor Red
  }
}
