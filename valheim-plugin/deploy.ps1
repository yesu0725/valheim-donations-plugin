# Build the plugin and deploy the DLL to all destinations (two client
# r2modman profiles + the dedicated server).
# Deploys into a Thunderstore-manager-style subfolder
# (BepInEx/plugins/TaegukGaming-Valheim_Donations/), matching how every other
# mod on these profiles/server is organized and how the Thunderstore package
# itself unpacks — not a flat file directly in BepInEx/plugins.
#
# Usage:  pwsh ./deploy.ps1        (from valheim-plugin/)
#         pwsh ./deploy.ps1 -NoBuild   (skip build, just copy the existing DLL)
param([switch]$NoBuild)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$dll  = Join-Path $here 'bin\Release\ValheimDonationSystem.dll'

# Destinations, kept in lockstep:
#   "Hearthbound Server"        - the LIVE profile actually launched/played
#                                 (was briefly the typo'd "Heathbound Server";
#                                  an even older "Hearthbound Valheim" entry here
#                                  pointed at a profile that never existed and
#                                  silently SKIP'd, which is how the played
#                                  profile went un-updated - see docs/OPERATIONS.md).
#   "Hearthbound Valheim - Test" - the testing profile.
#   dedicated server            - the headless server.
# Any missing folder is skipped, so this is safe if one isn't on a given machine
# -- but a SKIP on the profile you actually play means a STALE plugin. If you
# rename a profile in r2modman, update the matching path here too.
$pluginFolders = @(
  'C:\Users\yesu0725\AppData\Roaming\r2modmanPlus-local\Valheim\profiles\Hearthbound Server\BepInEx\plugins',
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

  # This script deploys the DLL only, never config. A profile can have a current
  # DLL but a placeholder valcoin_config.json (never set up), which makes the
  # in-game panel show "Offline" — exactly the 2026-07-20 incident. Warn on it
  # here so a stale/unconfigured profile is caught at deploy time, not in-game.
  # Read-only: never writes the token; that's a one-time manual setup.
  $cfg = Join-Path (Split-Path $pluginFolder) 'config\valcoin_config.json'
  if (Test-Path $cfg) {
    $c = Get-Content $cfg -Raw
    if ($c -match 'your-app\.fly\.dev' -or $c -match 'paste-the-') {
      Write-Host "  WARN: $cfg still has PLACEHOLDER backend_url/plugin_token — this profile will show Offline until you set the real values." -ForegroundColor Yellow
    }
  } else {
    Write-Host "  note: no valcoin_config.json yet at $cfg (the plugin writes a template on first launch)." -ForegroundColor DarkGray
  }
}
