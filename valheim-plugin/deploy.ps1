# Build the plugin and deploy the DLL to BOTH destinations (client + server).
# Deploys into a Thunderstore-manager-style subfolder
# (BepInEx/plugins/TaegukGaming-Valheim_Donations/), matching how every other
# mod on this server/profile is organized and how the Thunderstore package
# itself unpacks — not a flat file directly in BepInEx/plugins.
#
# Usage:  pwsh ./deploy.ps1        (from valheim-plugin/)
#         pwsh ./deploy.ps1 -NoBuild   (skip build, just copy the existing DLL)
param([switch]$NoBuild)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$dll  = Join-Path $here 'bin\Release\ValheimDonationSystem.dll'

$pluginFolders = @(
  'C:\Users\yesu0725\AppData\Roaming\r2modmanPlus-local\Valheim\profiles\Hearthbound Valheim - Test\BepInEx\plugins',
  'C:\Program Files (x86)\Steam\steamapps\common\Valheim dedicated server\BepInEx\plugins'
)
$subfolderName = 'TaegukGaming-Valheim_Donations'

if (-not $NoBuild) {
  Write-Host "Building (Release)..." -ForegroundColor Cyan
  dotnet build -c Release (Join-Path $here 'ValheimDonationSystem.csproj') | Out-Host
  if ($LASTEXITCODE -ne 0) { throw "Build failed — not deploying." }
}

if (-not (Test-Path $dll)) { throw "DLL not found: $dll" }

foreach ($pluginFolder in $pluginFolders) {
  if (-not (Test-Path $pluginFolder)) { Write-Host "SKIP (missing folder): $pluginFolder" -ForegroundColor Yellow; continue }

  # Clean up a stray flat copy from older deploy.ps1 versions — leaving both
  # would load the plugin twice (duplicate BepInPlugin GUID).
  $staleFlatCopy = Join-Path $pluginFolder 'ValheimDonationSystem.dll'
  if (Test-Path $staleFlatCopy) {
    Remove-Item $staleFlatCopy -Force
    Write-Host "removed stale flat copy -> $staleFlatCopy" -ForegroundColor DarkYellow
  }

  $targetDir = Join-Path $pluginFolder $subfolderName
  if (-not (Test-Path $targetDir)) { New-Item -ItemType Directory -Path $targetDir | Out-Null }
  $dest = Join-Path $targetDir 'ValheimDonationSystem.dll'

  try {
    Copy-Item $dll $dest -Force
    Write-Host "deployed -> $dest" -ForegroundColor Green
  } catch {
    # Most common cause: the dedicated server is running and holds the DLL.
    Write-Host "FAILED  -> $dest" -ForegroundColor Red
    Write-Host "         ($($_.Exception.Message)) — is the dedicated server running? Stop it and re-run with -NoBuild." -ForegroundColor Red
  }
}
