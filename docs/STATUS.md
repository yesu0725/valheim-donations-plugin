# Status Snapshot

This file decays fastest of anything in `docs/` — check the actual source
files before trusting it if it's been a while.

- **Project phase:** 6 — backend is **live on Fly.io** with **all four
  providers configured** (Ko-fi, Patreon, PayPal, PayMongo), plugin catalog
  syncs to remote clients, first Thunderstore package built, chat/console
  commands removed in favor of UI-only.
- **Backend version:** `0.5.0` (see [main.py](../backend/app/main.py)).
  **Deployed and reachable at `https://valheim-donations.fly.dev`** (see
  [DEPLOYMENT.md](DEPLOYMENT.md) for the live config).
- **Plugin version:** `5.3.0` (see [Plugin.cs:13](../valheim-plugin/Plugin.cs)).
  Deployed to both the client (`Hearthbound Valheim - Test` profile) and the
  dedicated server, and packaged for Thunderstore (see
  [Thunderstore files/Valheim_Donations/](../Thunderstore%20files/Valheim_Donations/)).
- **Backend tests:** 11 test files; README claims 39 tests passing. `pytest`
  is not installed in the base Python env — install
  `requirements-dev.txt` in a venv first (see [DEVELOPMENT.md](DEVELOPMENT.md)).
- **Last code activity:** 2026-07-09 (merged F4 Codex + F8 panel into one
  F4-only panel, reworked Donate tab with inline code/copy/portal/cooldown/
  terms modal, purged all emoji, removed F8 hotkey, chat/console command
  removal, Fly.io go-live).
- **Deployment target:** Fly.io, region `sin` (Singapore), 256 MB shared VM,
  1 GB persistent volume for SQLite. **Live** — see [DEPLOYMENT.md](DEPLOYMENT.md).

## Known discrepancies

### F4 Codex + F8 panel merged into one panel (2026-07-09)

`DonationCodex.cs` is deleted; `DonationPanel.cs` is now the single combined
panel, opened with **F4** (`codex_toggle_key`). The F8 hotkey was removed
entirely per user request (`ui_toggle_key` is now unused). Tabs: Donate,
Shop, Gift, Patrons, Admin. The Donate tab shows the code inline (Copy button
+ Open-portal button via `Application.OpenURL`), enforces a 30s client-side
cooldown, and has a Terms of Use modal. All emoji were removed from the UI and
every server reply string — Valheim's IMGUI font renders emoji as blank
squares. Donate replies now use a structured `__DONATE__:code|url|ttl` /
`__DONATE_ERR__:msg` wire format (see `Flows.cs` /
`DonationPanel.OnServerMessage`).

### Client r2modman profile is "Hearthbound Valheim" (not "- Test")

The live client runs the **`Hearthbound Valheim`** r2modman profile, not
`Hearthbound Valheim - Test`. `deploy.ps1` originally targeted the `- Test`
profile, so several deploys landed in the wrong place and the running client
stayed on a stale DLL with placeholder config (showed "Offline"). `deploy.ps1`
now targets the correct profile. If "Offline" recurs, first confirm which
profile is actually launched and that its `valcoin_config.json` has the live
`backend_url` + `plugin_token`.

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

### Provider rollout — all four providers live (2026-07-10)

Live on Fly secrets: **Ko-fi**, **Patreon**, **PayPal**, and **PayMongo**. Each
webhook returns **401** to a bad-signature probe (configured & verifying) rather
than **503** (unconfigured). Full secret list in
[DEPLOYMENT.md](DEPLOYMENT.md#live-status-2026-07-10); per-provider setup in
[PROVIDERS.md](PROVIDERS.md).

Per-provider notes worth remembering:

- **Ko-fi** — the only provider tested end-to-end with a (synthetic) webhook
  through to in-game delivery. Code rides in the message field.
- **Patreon** — payments carry no claim code, so a first-time patron must click
  **"Link my Patreon account"** on the portal once (OAuth); renewals auto-credit
  thereafter via `provider_links`.
- **PayPal** — auto-credit requires `PAYPAL_BUSINESS_EMAIL`. The portal builds a
  `paypal.com/donate/?business=…&custom=<code>` link so the claim code returns
  as `resource.custom` on the `PAYMENT.SALE.COMPLETED` / `PAYMENT.CAPTURE.COMPLETED`
  webhook. **Untested risk:** some newer PayPal accounts force fixed "hosted
  buttons" that can't carry a per-donor `custom`; if this account is one of
  those, auto-credit won't fire and donations land as `unmatched` (credit them
  via `POST /api/admin/credit-unmatched`). Confirm with one real ~$1 donation.
- **PayMongo** — the tightest flow: the portal mints a PaymentLink server-side
  with `metadata.claim_code` baked in, so the code is guaranteed to travel. Live
  `sk_live` key verified by minting a real (unpaid) ₱100 PaymentLink. Covers
  GCash + Maya + GrabPay + cards in one integration; priced in PHP.
  **Bug fixed during rollout (backend `0a69502`):** `_provider_links` set
  `out["paymongo"] = {}`, but the template gates the card on
  `{% if providers.paymongo %}` and an empty dict is falsy in Jinja, so the card
  stayed hidden whenever PayMongo was configured — now `{"enabled": True}`.

**Live-money testing was deliberately skipped** for PayPal and PayMongo per user
decision (2026-07-10). They are verified configured (401 probes, PayMongo link
mint) but no real charge has flowed through either yet.

## Regenerating this snapshot

Most of the facts above are also computed programmatically by
[scripts/generate_setup_guide.py](../scripts/generate_setup_guide.py) when
it builds [SETUP_GUIDE.pdf](SETUP_GUIDE.pdf). If this file and the PDF ever
disagree, trust the PDF (or re-run the script) — this file is maintained by
hand.
