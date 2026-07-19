# Thunderstore Packaging

The plugin ships as a **single Thunderstore package** — unlike ServerGuard's
client/server split, `ValheimDonationSystem.dll` is the same file for both
roles (server-required, client-optional for the F8/F4 UI). Packaging files
live outside the repo's tracked source at
[`Thunderstore files/Valheim_Donations/`](../Thunderstore%20files/Valheim_Donations/)
(git-ignored — see below).

## Package contents

| File | Purpose |
|---|---|
| `manifest.json` | Thunderstore metadata: name, description (≤250 chars), `version_number`, `website_url`, `dependencies` |
| `README.md` | Shown on the Thunderstore listing page |
| `CHANGELOG.md` | Shown on the Thunderstore listing page |
| `icon.png` | **Must be exactly 256×256**, RGBA PNG |
| `ValheimDonationSystem.dll` | The built plugin |

`manifest.json`'s `dependencies` currently pin:
- `denikson-BepInExPack_Valheim-5.4.2333`
- `ValheimModding-JsonDotNET-13.0.4`

(No `YamlDotNet` dependency — the shop/admin YAML parsers are a built-in
regex parser, not a real YAML library.)

Shop **preview images are not packaged** — they're operator content referenced
by `preview_image` in `valcoin_shop.yaml`, not mod assets. Ship them as URLs so
clients fetch them at runtime.

## Release checklist

1. Bump the version in **two places** and keep them in sync:
   - `valheim-plugin/Plugin.cs` → the `[BepInPlugin(...)]` attribute's third arg
   - `Thunderstore files/Valheim_Donations/manifest.json` → `version_number`
   - (Optionally also update `docs/STATUS.md`'s plugin version line.)
2. Rebuild: `cd valheim-plugin; dotnet build -c Release`
3. Deploy to client + server as usual (`deploy.ps1` — see [PLUGIN.md](PLUGIN.md)).
4. Copy the fresh DLL into the package folder:
   ```powershell
   Copy-Item valheim-plugin\bin\Release\ValheimDonationSystem.dll `
     "Thunderstore files\Valheim_Donations\ValheimDonationSystem.dll" -Force
   ```
5. Add a new entry to `Thunderstore files/Valheim_Donations/CHANGELOG.md`.
6. Zip the five files **flat at the zip root** (a manifest nested in a
   subfolder is rejected by Thunderstore):
   ```powershell
   $src = "Thunderstore files\Valheim_Donations"
   $files = @("manifest.json","README.md","CHANGELOG.md","icon.png","ValheimDonationSystem.dll")
   Compress-Archive -Path ($files | ForEach-Object { Join-Path $src $_ }) `
     -DestinationPath "Thunderstore files\Valheim_Donations-v<X.Y.Z>_<timestamp>.zip"
   ```
7. Upload at https://thunderstore.io/c/valheim/create/docs/ (or via `tcli`).

## Icon

`icon.png` was generated from a "rune-carved Viking drinking horn pouring gold
coins" concept — thematically distinct from a generic coin/piggy-bank icon,
matches Valheim's material language (worn wood, bronze, gold), and reads
clearly at 32×32. Regenerate at 256×256 RGBA if it's ever replaced.

## Why the package folder isn't committed

`Thunderstore files/` is **not tracked in git** — its `.dll` is caught by the
repo's blanket `*.dll` `.gitignore` rule (the same rule that keeps
copyrighted Valheim/Unity assemblies out of `valheim-plugin/libs/`). This is
a deliberate difference from the ServerGuard repo, which does commit its
Thunderstore packages. Revisit if release history in git becomes valuable
enough to outweigh committing binary build artifacts.

## Source of truth

- [Thunderstore files/Valheim_Donations/manifest.json](../Thunderstore%20files/Valheim_Donations/manifest.json)
- [valheim-plugin/Plugin.cs](../valheim-plugin/Plugin.cs) — version string
