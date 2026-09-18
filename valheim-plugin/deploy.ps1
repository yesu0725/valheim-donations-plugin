# Build the plugin and deploy the DLL to the "HB Test" Gale profile - and ONLY
# that profile. Deploys into a Thunderstore-manager-style subfolder
# (BepInEx/plugins/TaegukGaming-Valheim_Donations/), matching how every other
# mod on this profile is organized and how the Thunderstore package itself
# unpacks - not a flat file directly in BepInEx/plugins.
#
# Usage:  pwsh ./deploy.ps1        (from valheim-plugin/)
#         pwsh ./deploy.ps1 -NoBuild   (skip build, just copy the existing DLL)
param([switch]$NoBuild)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$dll  = Join-Path $here 'bin\Release\ValheimDonationSystem.dll'

# THE ONLY DEPLOY DESTINATION - the "HB Test" profile in Gale.
#
# Owner's instruction, given 2026-08-07 and REAFFIRMED 2026-09-18: every new
# build goes to HB Test and to no other profile. Everything else - the played
# profiles, the old r2modman profiles, the dedicated server - is promoted by
# hand, deliberately, after the build has been tested. A routine build must
# never reach the profile the owner actually plays on.
#
# WHY THIS SCRIPT DELETES BEFORE IT COPIES. Gale hard-links a mod's files across
# every profile that has the same version installed (measured 2026-08-31 and
# again 2026-09-01/09; which profiles share an inode changes as Gale re-links).
# Copy-Item overwrites a file's CONTENTS, so copying onto a linked file writes
# through the link into every other profile sharing it - which is exactly how
# the "do not touch any other profile" rule was being broken from 2026-08-17
# until today without anyone intending it. Remove-Item first unlinks HB Test's
# directory entry (the other profiles keep their data), and the copy then
# creates a file that is HB Test's alone. The write cannot propagate.
#
# Between 2026-09-01 and 2026-09-18 a verification pass at the bottom of this
# script went further the other way and copied into any profile whose hash did
# not match. That was well-intentioned (a stranded profile had cost a debugging
# round-trip) but it was the opposite of the owner's rule, and it is gone. The
# pass is now READ-ONLY: it reports what every profile is on so nobody has to
# guess, and writes nothing.
#
# Deliberately NOT deployed to, leave these alone:
#   - the played client profiles ("Hearthbound Valheim", "Hearthbound - Admin",
#     "HB Modpack Ref" and whatever they are renamed to next)
#   - the old r2modman profiles, now superseded
#     (%APPDATA%\r2modmanPlus-local\Valheim\profiles\)
#   - the dedicated server
#     (C:\Program Files (x86)\Steam\steamapps\common\Valheim dedicated server)
$galeProfiles  = 'C:\Users\yesu0725\AppData\Roaming\com.kesomannen.gale\valheim\profiles'
$testProfile   = 'HB Test'
$pluginFolder  = Join-Path $galeProfiles "$testProfile\BepInEx\plugins"
$subfolderName = 'TaegukGaming-Valheim_Donations'

if (-not $NoBuild) {
  Write-Host "Building (Release)..." -ForegroundColor Cyan
  dotnet build -c Release (Join-Path $here 'ValheimDonationSystem.csproj') | Out-Host
  if ($LASTEXITCODE -ne 0) { throw "Build failed - not deploying." }
}

if (-not (Test-Path $dll)) { throw "DLL not found: $dll" }

# Hard failure, never a silent skip. This used to warn-and-continue, which is
# exactly how a renamed profile left the target on a weeks-old DLL while the
# deploy still looked green - twice, costing whole debugging sessions chasing
# "fixes that don't work". With a single destination a skip means NOTHING was
# deployed, so it must be loud. If the profile was renamed, update $testProfile.
if (-not (Test-Path $pluginFolder)) {
  throw "Deploy target missing: $pluginFolder`n" +
        "  Was the Gale profile renamed, or is Gale installed elsewhere? Fix `$testProfile in deploy.ps1 - nothing was deployed."
}

# Snapshot every OTHER profile's DLL hash before touching anything. After the
# copy these must be unchanged - that is the proof the write stayed in HB Test,
# and it is checked rather than assumed because the hard link makes "I only
# copied to one path" mean nothing on its own.
function Get-ProfileDlls {
  Get-ChildItem $galeProfiles -Recurse -Filter 'ValheimDonationSystem.dll' -ErrorAction SilentlyContinue |
    ForEach-Object {
      [pscustomobject]@{
        Profile = $_.FullName.Replace("$galeProfiles\", '').Split('\')[0]
        Path    = $_.FullName
        Hash    = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
      }
    }
}
$before = @{}
Get-ProfileDlls | Where-Object { $_.Profile -ne $testProfile } | ForEach-Object { $before[$_.Profile] = $_.Hash }

# Clean up a stray flat copy from older deploy.ps1 versions - leaving both
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
  # Break the hard link BEFORE writing (see the header). Deleting one link of a
  # hard-linked file removes only this directory entry; the other profiles keep
  # their bytes. The copy that follows is then HB Test's own file.
  if (Test-Path $dest) { Remove-Item $dest -Force }
  Copy-Item $dll $dest -Force
  Write-Host "deployed -> $dest" -ForegroundColor Green
} catch {
  # Most common cause: Valheim is running from this profile and holds the DLL.
  Write-Host "FAILED  -> $dest" -ForegroundColor Red
  Write-Host "         ($($_.Exception.Message)) - is Valheim running? Close it and re-run with -NoBuild." -ForegroundColor Red
  throw
}

# This script deploys the DLL only, never config. A profile can have a current
# DLL but a placeholder valcoin_config.json (never set up), which makes the
# in-game panel show "Offline" - exactly the 2026-07-20 incident. Warn on it
# here so a stale/unconfigured profile is caught at deploy time, not in-game.
# Read-only: never writes the token; that's a one-time manual setup.
$cfg = Join-Path (Split-Path $pluginFolder) 'config\valcoin_config.json'
if (Test-Path $cfg) {
  $c = Get-Content $cfg -Raw
  if ($c -match 'your-app\.fly\.dev' -or $c -match 'paste-the-') {
    Write-Host "  WARN: $cfg still has PLACEHOLDER backend_url/plugin_token - this profile will show Offline until you set the real values." -ForegroundColor Yellow
  }
} else {
  Write-Host "  note: no valcoin_config.json yet at $cfg (the plugin writes a template on first launch)." -ForegroundColor DarkGray
}

# --- report (READ-ONLY): what every profile is on, and proof nothing else moved
#
# Writes nothing. The point is that nobody has to guess which build a profile
# runs - that guess has cost debugging sessions before - and that "the other
# profiles were not touched" is demonstrated, not asserted.
$want = (Get-FileHash $dll -Algorithm SHA256).Hash
Write-Host ""
Write-Host "Gale profiles (read-only; build is $($want.Substring(0,12))...):" -ForegroundColor Cyan
$leaked = @()
Get-ProfileDlls | Sort-Object Profile | ForEach-Object {
  $isTest = $_.Profile -eq $testProfile
  $onBuild = $_.Hash -eq $want
  if ($isTest) {
    $tag = if ($onBuild) { 'DEPLOYED' } else { 'MISMATCH' }
    $color = if ($onBuild) { 'Green' } else { 'Red' }
  } else {
    $unchanged = $before.ContainsKey($_.Profile) -and $before[$_.Profile] -eq $_.Hash
    if (-not $unchanged) { $leaked += $_.Profile }
    $tag = if ($unchanged) { 'untouched' } else { 'CHANGED!' }
    $color = if ($unchanged) { 'DarkGray' } else { 'Red' }
  }
  Write-Host ("  {0,-10} {1,-24} {2}" -f $tag, $_.Profile, $_.Hash.Substring(0,12)) -ForegroundColor $color
}
if ($leaked.Count -gt 0) {
  # Should be impossible after the Remove-Item above; if it ever prints, the
  # link-breaking did not work and the rule was violated - say so loudly.
  Write-Host "  !! the deploy leaked into: $($leaked -join ', ') - the hard link was NOT broken. Investigate before trusting this script again." -ForegroundColor Red
}

# The dedicated server is deliberately NOT touched here; promoting to it stays a
# manual, deliberate step. Check it by hand when a fix is server-side:
#   Get-FileHash "C:\Program Files (x86)\Steam\steamapps\common\Valheim dedicated server\BepInEx\plugins\TaegukGaming-Valheim_Donations\ValheimDonationSystem.dll"
