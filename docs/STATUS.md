# Status Snapshot

This file decays fastest of anything in `docs/` — check the actual source
files before trusting it if it's been a while.

- **Project phase:** 6 — backend is **live on Fly.io**, Ko-fi wired end-to-end,
  plugin catalog now syncs to remote clients, first Thunderstore package built,
  chat/console commands removed in favor of UI-only.
- **Backend version:** `0.5.0` (see [main.py](../backend/app/main.py)).
  **Deployed and reachable at `https://valheim-donations.fly.dev`** (see
  [DEPLOYMENT.md](DEPLOYMENT.md) for the live config).
- **Plugin version:** `5.2.0` (see [Plugin.cs:13](../valheim-plugin/Plugin.cs)).
  Deployed to both the client (`Hearthbound Valheim - Test` profile) and the
  dedicated server, and packaged for Thunderstore (see
  [Thunderstore files/Valheim_Donations/](../Thunderstore%20files/Valheim_Donations/)).
- **Backend tests:** 11 test files; README claims 39 tests passing. `pytest`
  is not installed in the base Python env — install
  `requirements-dev.txt` in a venv first (see [DEVELOPMENT.md](DEVELOPMENT.md)).
- **Last code activity:** 2026-07-09 (chat/console command removal, F8 Admin
  tab, Fly.io go-live, Thunderstore packaging).
- **Deployment target:** Fly.io, region `sin` (Singapore), 256 MB shared VM,
  1 GB persistent volume for SQLite. **Live** — see [DEPLOYMENT.md](DEPLOYMENT.md).

## Known discrepancies

### Chat/console commands removed — BREAKING (2026-07-09)

`ChatSlashPatch.cs` is deleted. All donation actions (`/donate`, `/coins`,
`/shop`, `/buy`, `/gift`, `/topdonors`, `/title`, `/givecoins`,
`/removecoins`) now exist **only** as F4 Codex / F8 panel buttons over the
`vc_action` RPC — there is no chat or console fallback. Reason: the
reflection-based `Chat.RPC_ChatMessage` hook was unreliable alongside other
chat-patching mods on this server (root cause not fully diagnosed — `/donate`
silently did nothing, no errors logged). A lightweight `ChatDecoration.cs`
still prefixes chat with the donor badge/title cosmetic, but doesn't parse
commands.

**Consequence:** the plugin is now required **client-side**, not just
server-side — a vanilla client can no longer use the donation system at all.
On this server that's moot (ServerGuard already kicks vanilla clients), but
it's a real behavior change for the public Thunderstore listing. See
[SHOP.md](SHOP.md#no-chat-or-console-commands).

New: an **Admin tab** in the F8 panel (give/remove balance) replaces the
removed `/givecoins`/`/removecoins`, gated on a `whoami` RPC check against
`valcoin_admins.yaml`.

### Remote/vanilla clients need the catalog broadcast to arrive — RESOLVED (2026-07-08)

`Catalog.cs` only ever loaded `valcoin_shop.yaml` from whichever machine had
the file (the dedicated server). Remote clients — including vanilla ones —
saw an empty shop. `CatalogSync.cs` now broadcasts the parsed catalog to every
connected client every 30s over a new `vc_catalog` RPC
(`Catalog.Serialize()` / `Catalog.ApplyRemote()`, in-memory only, never
written to the remote client's disk). See [PLUGIN.md](PLUGIN.md).

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

### Placeholder Fly app name — RESOLVED (2026-07-05)

The placeholder name `valheim-donations` turned out to be unclaimed, so
`flyctl launch --no-deploy` kept it as-is — the live app is
`https://valheim-donations.fly.dev`. See [DEPLOYMENT.md](DEPLOYMENT.md).

### Provider rollout — Ko-fi only so far

Only `KOFI_VERIFICATION_TOKEN` / `KOFI_USERNAME` are set as Fly secrets.
PayPal, Patreon, and PayMongo routes are deployed but 503 until their secrets
are added — see [PROVIDERS.md](PROVIDERS.md) and the "Per-provider secrets"
section of [DEPLOYMENT.md](DEPLOYMENT.md).

## Regenerating this snapshot

Most of the facts above are also computed programmatically by
[scripts/generate_setup_guide.py](../scripts/generate_setup_guide.py) when
it builds [SETUP_GUIDE.pdf](SETUP_GUIDE.pdf). If this file and the PDF ever
disagree, trust the PDF (or re-run the script) — this file is maintained by
hand.
