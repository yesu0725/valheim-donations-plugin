# Status Snapshot

This file decays fastest of anything in `docs/` — check the actual source
files before trusting it if it's been a while.

- **Project phase:** 6+ — backend is **live on Fly.io** with **all four
  providers configured** (Ko-fi, Patreon, PayPal, PayMongo), plugin catalog
  syncs to remote clients, chat/console commands removed in favor of UI-only.
  Shop now ships a **Soulkeeper Charm** consumable (backend charge ledger +
  in-game skill-save + Valkyrie tombstone carry); cosmetic badge/title/flair
  perks were dropped.
- **Backend version:** `0.6.0` (see [main.py](../backend/app/main.py)).
  **Deployed and reachable at `https://valheim-donations.fly.dev`** — redeployed
  2026-07-19 adding **`coins_per_usd` to `/api/state`** (from
  `settings.coins_per_unit["USD"]`, currently **50**) so the in-game panel can
  show an authoritative exchange rate instead of hard-coding one. Verified live:
  `/api/state/<id>` returns `coins_per_usd: 50.0`. The 2026-07-13 deploy added
  the **weekly charge cap** (`charge_grants` history table, `weekly_charge_cap`
  on `/api/spend`, enforced across all SKUs of a charge kind) on top of the
  **charge ledger** (`charges` table, `grant_charges` on `/api/spend`,
  `/api/charges/consume`, and `charges` + `owned_skus` + `weekly_usage` on
  `/api/state`). See [DEPLOYMENT.md](DEPLOYMENT.md).
- **Plugin version:** `5.16.0` (see [Plugin.cs:13](../valheim-plugin/Plugin.cs)).
  Deployed to the `Hearthbound Valheim - Test` r2modman profile and the
  dedicated server via `deploy.ps1`. NOTE: the `Hearthbound Valheim` (non-test)
  profile **no longer exists on this machine** and is now silently skipped by
  `deploy.ps1` — recreate it or drop it from the script's list.
  **Restart the server + client to load 5.16.0.**
  **5.16.0** adds **shop preview images** (optional `preview_image` per SKU —
  `https` URL or a path relative to `BepInEx/config`; loaded async and cached by
  [ImageCache.cs](../valheim-plugin/ImageCache.cs)), a **click-to-enlarge zoom
  overlay** (fits to 80% of the window, never upscales past 1:1, closes on
  Close / click-outside / Escape, and outranks the other modals so it can be
  opened from the buy dialog), and the **`$1 USD = N Valcoins` callout** on the
  Donate tab fed by the backend's new `coins_per_usd`. The live Familiars
  catalog sets `preview_image` for all 8 SKUs. (5.7.0 = Soulkeeper Charm
  Phases 1+2; 5.8.0 = grouped/categorized Shop tab; 5.9.0 = native Valheim-style
  panel skin + Yes/Cancel purchase-confirm modal; 5.10.0 = `armor_vfx` auras;
  5.11–5.12 iterated slot-bound auras; 5.13.0 pivoted the category to
  **Familiars** — 8 mini flying-creature companions (Bat / Ghost / Deathsquito /
  Drake / Wraith / Volture / Gjall / Fallen Valkyrie) hovering at the right
  shoulder, bound to the equipped helmet, tier-priced 400–1300c; whole-creature
  visuals cloned inside an inactive holder, stripped of AI/network/physics.
  **5.14.0** fixed the familiar clones (dependency-ordered strip so no more
  "Can't remove Humanoid" log; `flying` animator bool so the Hatchling animates;
  height/particle tuning; spawn/despawn poof) and added **feather fall** (shared
  with the Feather Cape). **5.15.0** adds a **small flat attack bonus per
  familiar** (`SE_FamiliarBond` on the game's `ModifyAttack`: +2/+3 of the
  creature's damage type), an **overwrite warning** in the buy modal when the
  helmet already has a familiar, **Gjall drips removed**, the **10-charge/week
  Soulkeeper cap**, and a **tomb-area creature repel** on Valkyrie landing.
  Server's `valcoin_shop.yaml` still has 20 SKUs — restart to reload. NOTE:
  server DLL copies require the dedicated server to be STOPPED first.)
- **Backend tests:** 65 passing (`cd backend; pytest`) — includes 3 new weekly
  charge-cap tests. `pytest` is not in the base Python env — install
  `requirements-dev.txt` in a venv first (see [DEVELOPMENT.md](DEVELOPMENT.md)).
- **Last code activity:** 2026-07-19 — shop **preview images** + **click-to-
  enlarge** overlay, **exchange-rate callout** on the Donate tab, backend
  `coins_per_usd` on `/api/state`; plugin bumped to 5.16.0 and backend to 0.6.0,
  both deployed. Added a `UnityEngine.UnityWebRequestTextureModule` reference to
  the csproj (copied into `libs/` from the Steam install) — required for
  `UnityWebRequestTexture`/`DownloadHandlerTexture`.
- **Deployment target:** Fly.io, region `sin` (Singapore), 256 MB shared VM,
  1 GB persistent volume for SQLite. **Live** — see [DEPLOYMENT.md](DEPLOYMENT.md).

## Known discrepancies

### Preview images use config-relative paths — remote clients see blanks (2026-07-19)

The live `valcoin_shop.yaml` (identical on the dedicated server and the test
profile) sets `preview_image` for all 8 Familiars as **config-relative paths**
(`shop_images/Bat.png`, …), and the 8 PNGs exist in `BepInEx/config/shop_images/`
in **both** those locations. But the catalog RPC syncs the *string*, not the
file: a connecting player resolves `shop_images/Bat.png` against **their own**
`BepInEx/config` and — having no such folder — gets a blank space where the
thumbnail should be. Only this machine renders them.

**Fix:** host the 8 PNGs at `https` URLs and point `preview_image` at those.
Worth downscaling first — the sources are 300–780 KB each (~3.5 MB total) for
images that render at 72px in the shop row, so every client would pay that
download for nothing.

### `deploy.ps1` silently skips a profile that no longer exists (2026-07-19)

`deploy.ps1` lists three destinations; the `Hearthbound Valheim` (non-test)
r2modman profile is **gone from this machine**, so every deploy prints
`SKIP (missing folder)` and copies to only two. That's by design (the script
tolerates missing folders), but it means "deployed" now covers the test profile
and the dedicated server only. Recreate the profile or prune the list.

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
with logos). Catalog is **20 SKUs** (3 Soulkeeper tiers + 8 Familiars + 9 `grant_item`
bundles). See [SHOP.md](SHOP.md).

> **Follow-up (2026-07-13, plugin 5.14–5.15, backend redeployed):** Familiars
> gained light utility — each grants **feather fall** (the Feather Cape's own
> `SlowFall` effect, non-stacking) and a **small flat attack bonus**
> (`SE_FamiliarBond`, +2/+3 of the creature's damage type via the game's
> `ModifyAttack`; 1–4 % of endgame weapon damage — inside the balance rule). The
> buy modal now **warns before overwriting** a familiar already on the equipped
> helmet. The Soulkeeper pool is capped at **10 charges/player/week** (shared
> across the ×1/×5/×10 tiers), enforced backend-side from a new `charge_grants`
> table via `weekly_charge_cap`. On Valkyrie landing, three shockwave pulses
> **repel hostile creatures** from the tombstone (zero-damage, no-attacker
> pushback — no aggro). Familiar-clone fixes: dependency-ordered component strip
> (no more "Can't remove Humanoid"), `flying` animator bool (Hatchling animates),
> Gjall tar drips removed, particle sizes tamed, spawn/despawn poof.

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
