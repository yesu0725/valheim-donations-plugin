# Build the plugin and deploy the DLL to the TEST Gale profile ONLY.
# Deploys into a Thunderstore-manager-style subfolder
# (BepInEx/plugins/TaegukGaming-Valheim_Donations/), matching how every other
# mod on this profile is organized and how the Thunderstore package itself
# unpacks — not a flat file directly in BepInEx/plugins.
#
# Usage:  pwsh ./deploy.ps1        (from valheim-plugin/)
#         pwsh ./deploy.ps1 -NoBuild   (skip build, just copy the existing DLL)
param([switch]$NoBuild)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$dll  = Join-Path $here 'bin\Release\ValheimDonationSystem.dll'

# THE ONLY DEPLOY DESTINATION - the "HB Test" profile in Gale
# (owner's instruction, 2026-08-07: deploy to the test profile and DO NOT TOUCH
# any other profile; 2026-08-17: moved off r2modman to Gale, which stores
# profiles under %APPDATA%\com.kesomannen.gale\valheim\profiles\).
#
# CAVEAT, measured 2026-08-31: "do not touch any other profile" is NOT what
# happens under Gale. Gale hard-links mod files across profiles - all four
# profiles here share ONE inode for this DLL (verify with `fsutil file
# queryfileid` on each; the ids match). Copy-Item overwrites a file's contents,
# so this write goes through the link and every profile holding this mod gets
# the new DLL, the played one included.
#
# Left as-is deliberately: it means the played profile is never stranded on a
# stale DLL, which is the exact failure that cost two debugging sessions under
# r2modman. To actually isolate the test profile, Remove-Item the destination
# before copying - that breaks the link and gives it a file of its own. See the
# Gale hard-link note in docs/OPERATIONS.md.
#
# Deliberately NOT deployed to any more, leave these alone:
#   - the live/played client profile (currently "HB Server", previously
#     "Hearthbound Server" / "Hearthbound Valheim" - it keeps getting renamed)
#   - the old r2modman profiles, now superseded
#     (%APPDATA%\r2modmanPlus-local\Valheim\profiles\)
#   - the dedicated server
#     (C:\Program Files (x86)\Steam\steamapps\common\Valheim dedicated server)
# Promoting a tested build to those is a manual, deliberate step now - not
# something a routine build should do behind your back.
$pluginFolders = @(
  'C:\Users\yesu0725\AppData\Roaming\com.kesomannen.gale\valheim\profiles\HB Test\BepInEx\plugins'
)
$subfolderName = 'TaegukGaming-Valheim_Donations'

if (-not $NoBuild) {
  Write-Host "Building (Release)..." -ForegroundColor Cyan
  dotnet build -c Release (Join-Path $here 'ValheimDonationSystem.csproj') | Out-Host
  if ($LASTEXITCODE -ne 0) { throw "Build failed — not deploying." }
}

if (-not (Test-Path $dll)) { throw "DLL not found: $dll" }

foreach ($pluginFolder in $pluginFolders) {
  # Hard failure, never a silent skip. This used to warn-and-continue, which is
  # exactly how a renamed profile left the target on a weeks-old DLL while the
  # deploy still looked green - twice, costing whole debugging sessions chasing
  # "fixes that don't work". With a single destination a skip means NOTHING was
  # deployed, so it must be loud. If the profile was renamed, update the path
  # above.
  if (-not (Test-Path $pluginFolder)) {
    throw "Deploy target missing: $pluginFolder`n" +
          "  Was the Gale profile renamed, or is Gale installed elsewhere? Fix the path in deploy.ps1 - nothing was deployed."
  }

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
