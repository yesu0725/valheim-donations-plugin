# Status Snapshot

This file decays fastest of anything in `docs/` — check the actual source
files before trusting it if it's been a while.

- **Project phase:** 6+ — backend is **live on Fly.io** with **all four
  providers configured** (Ko-fi, Patreon, PayPal, PayMongo), plugin catalog
  syncs to remote clients, chat/console commands removed in favor of UI-only.
  Shop now ships a **Soulkeeper Charm** consumable (backend charge ledger +
  in-game skill-save + Valkyrie tombstone carry); cosmetic badge/title/flair
  perks were dropped.
- **Backend version:** `0.5.0` (see [main.py](../backend/app/main.py)).
  **Deployed and reachable at `https://valheim-donations.fly.dev`.** The live
  build includes the **charge ledger** (`charges` table, `grant_charges` on
  `/api/spend`, `/api/charges/consume`, and `charges` + `owned_skus` +
  `weekly_usage` on `/api/state`) — deployed 2026-07-12. See
  [DEPLOYMENT.md](DEPLOYMENT.md).
- **Plugin version:** `5.7.0` (see [Plugin.cs:13](../valheim-plugin/Plugin.cs)).
  Deployed to both client r2modman profiles (`Hearthbound Valheim` +
  `Hearthbound Valheim - Test`) and the dedicated server via `deploy.ps1`.
  **Restart the server + client to load 5.7.0.**
- **Backend tests:** 62 passing (`cd backend; pytest`). `pytest` is not in the
  base Python env — install `requirements-dev.txt` in a venv first (see
  [DEVELOPMENT.md](DEVELOPMENT.md)).
- **Last code activity:** 2026-07-12 — Soulkeeper Charm Phase 1 (charge ledger
  + warded death) and Phase 2 (Valkyrie carry), shop UI overhaul (owned /
  weekly-cap / charge states, input-blocking, larger fonts), portal single-
  column provider redesign, removal of the cosmetic chat-decoration subsystem.
- **Deployment target:** Fly.io, region `sin` (Singapore), 256 MB shared VM,
  1 GB persistent volume for SQLite. **Live** — see [DEPLOYMENT.md](DEPLOYMENT.md).

## Known discrepancies

### Soulkeeper Charm added; cosmetic perks + chat decoration removed (2026-07-12)

The shop's cosmetic `grant_perk` perks — `donor_badge`, `chat_title`,
`companion_flair`, `lordslayer_title` — are **removed from the catalog and
code**, and `ChatDecoration.cs` is **deleted**. On this dedicated-server build
their client-side rendering was unreliable (peer-to-peer chat routing, no
`NetworkUserId`), so repeated "badge/title still not working" reports were
architectural, not a bug to chase.

They're replaced by the **Soulkeeper Charm** consumable (`add_charges` effect):

- **Phase 1 (live):** buying credits a backend `charges` pool; on the local
  player's death one charge is consumed to **skip the skill drain**
  (`Soulkeeper.cs`, backed by `/api/charges/consume`). 3 decoy-priced tiers.
- **Phase 2 (prototype, needs live play-testing):** the same warded death also
  makes the intro Valkyrie **carry the player from the spawn point to their
  tombstone** on respawn (`ValkyrieCarry.cs`) — fade transition, ESC-menu +
  auto-pickup suppressed mid-flight, watchdog + plain-teleport fallback. First
  live test flew end-to-end; a post-flight `AutoPickup`/`FloatingTerrain` NRE
  was addressed by suspending auto-pickup during the carry (watch for recurrence).

Also this pass: shop UI now renders owned / weekly-cap / charge states from
`/api/state` (the client was previously blind to server-side ownership), all
game input is blocked while the panel is open, fonts enlarged, and the donor
portal is a single-column provider list (Ko-fi → PayMongo → Patreon → PayPal
with logos). Catalog is **12 SKUs** (3 Soulkeeper tiers + 9 `grant_item`
bundles). See [SHOP.md](SHOP.md).

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
silently did nothing, no errors logged).

> **Update (2026-07-12):** `ChatDecoration.cs` (the donor-badge/chat-title chat
> prefix) has since been **deleted** — see the Soulkeeper Charm entry below.

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

### Build artifact mismatch — benign ⚠

The plugin's compiled output in
[bin/Release/](../valheim-plugin/bin/Release) still contains stale
`Jotunn.dll` and `YamlDotNet.dll` copies, even though the current
[csproj](../valheim-plugin/ValheimDonationSystem.csproj) no longer references
them (both deps were dropped). They're left over from before the change and an
incremental build doesn't delete them. **This is harmless for deploys:**
`deploy.ps1` copies only `ValheimDonationSystem.dll`, not the folder, so the
stale DLLs never ship. When **packaging for Thunderstore**, do a clean build
(delete `bin/`) or hand-pick the DLL so the zip doesn't bundle them — see
[THUNDERSTORE.md](THUNDERSTORE.md).

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
