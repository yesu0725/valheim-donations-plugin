# Status Snapshot

This file decays fastest of anything in `docs/` — check the actual source
files before trusting it if it's been a while.

- **Project phase:** 5 (most recent — leaderboard + in-game IMGUI panel).
- **Backend version:** `0.5.0` (see [main.py](../backend/app/main.py)).
- **Plugin version:** `5.1.0` (see [Plugin.cs:13](../valheim-plugin/Plugin.cs)).
- **Backend tests:** 11 test files; README claims 39 tests passing. `pytest`
  is not installed in the base Python env — install
  `requirements-dev.txt` in a venv first (see [DEVELOPMENT.md](DEVELOPMENT.md)).
- **Last code activity:** mid-May 2026 (test files dated 2026-05-17).
- **Deployment target:** Fly.io, region `sin` (Singapore), 256 MB shared VM,
  1 GB persistent volume for SQLite. See [DEPLOYMENT.md](DEPLOYMENT.md).

## Known discrepancies

### Build artifact mismatch ⚠

The plugin's compiled output in
[bin/Release/net472/](../valheim-plugin/bin/Release/net472) includes
`Jotunn.dll` and `YamlDotNet.dll`, but the current
[csproj](../valheim-plugin/ValheimDonationSystem.csproj) no longer
references them (the [plugin README](../valheim-plugin/README.md) explicitly
notes both deps were dropped). The release DLL predates that change —
**rebuild before shipping.**

### Unity DLLs / reference set — RESOLVED (2026-07-04)

[libs/](../valheim-plugin/libs) is now a **version-consistent set** copied from
the current dedicated-server `valheim_server_Data/Managed/` (assembly_valheim +
UnityEngine + CoreModule + IMGUIModule + InputLegacyModule +
UnityWebRequestModule + **TextRenderingModule**, the last now referenced in the
csproj for `FontStyle`). The plugin builds clean against the **current** Valheim
(Ashlands) — `dotnet build -c Release` → `bin/Release/ValheimDonationSystem.dll`.

To build against a different Valheim version, replace the whole libs set from one
install at once (mixing versions causes cascade errors like `ZDOMan.GetMyID`
missing or the `BaseUnityPlugin.Config` name collision).

### Removed commands pruned (2026-07-04)

`/sethome`, `/home`, `/shout` (and the `sethome` / `shout` perks) are **removed
from code** — the handlers were deleted from `ChatSlashPatch.cs` and
`UiActionRouter.cs`, and the F8 panel's shout editor removed. `PerkManager` still
carries the now-unused home/charge helpers (harmless; prune later if desired).

### Placeholder Fly app name

[fly.toml](../backend/fly.toml)'s `app` field is still the placeholder
`valheim-donations`. `flyctl launch --no-deploy` rewrites it once you claim
a real name. See [DEPLOYMENT.md](DEPLOYMENT.md).

## Regenerating this snapshot

Most of the facts above are also computed programmatically by
[scripts/generate_setup_guide.py](../scripts/generate_setup_guide.py) when
it builds [SETUP_GUIDE.pdf](SETUP_GUIDE.pdf). If this file and the PDF ever
disagree, trust the PDF (or re-run the script) — this file is maintained by
hand.
