# Changelog

Release history for **both halves** of the system — the BepInEx plugin and the
FastAPI backend — in one place, because the two version independently but ship
against each other. Where a plugin feature needs a matching backend, the entry
says so explicitly; that pairing is the thing most likely to bite an operator
who upgrades one side only.

This is the **engineering-facing** record. The player-facing changelog shown on
the Thunderstore listing is
[`Thunderstore files/Valheim_Donations/CHANGELOG.md`](../Thunderstore%20files/Valheim_Donations/CHANGELOG.md)
— it covers the plugin only, in end-user language, and deliberately omits
backend/API detail. Keep the two in sync when cutting a release (see
[THUNDERSTORE.md](THUNDERSTORE.md)).

For "what is true right now" rather than "what changed", see
[STATUS.md](STATUS.md).

## Current versions

| Component | Version | Source of truth |
|---|---|---|
| Plugin | **5.16.0** | [`Plugin.cs`](../valheim-plugin/Plugin.cs) `[BepInPlugin]` 3rd arg |
| Backend | **0.6.0** | [`main.py`](../backend/app/main.py) `FastAPI(version=...)` |
| Thunderstore package | **5.16.0** | [`manifest.json`](../Thunderstore%20files/Valheim_Donations/manifest.json) `version_number` |

The plugin version lives in **four** places that must agree (`Plugin.cs`,
`manifest.json`, the package `README.md`, and [STATUS.md](STATUS.md)); the
[THUNDERSTORE.md](THUNDERSTORE.md) checklist enumerates them.

### Compatibility

The backend is **additive-only** on `/api/state` and `/api/spend`: new response
fields are ignored by older plugins, and new request fields are optional. So a
newer backend is always safe with an older plugin. The reverse is not true — a
newer plugin can ask for something an old backend doesn't serve:

| Plugin needs | Minimum backend | Symptom if too old |
|---|---|---|
| Exchange-rate callout (`coins_per_usd`) | 0.6.0 | Donate tab reads "Exchange rate unavailable" |
| Weekly charge cap (`weekly_charge_cap`) | 0.6.0 | Cap silently unenforced — charges never rejected |
| Charge ledger / Soulkeeper (`grant_charges`, `/api/charges/consume`) | 0.5.0 | Purchases fail; no charges credited |
| Shop owned/weekly state (`owned_skus`, `weekly_usage`) | 0.5.0 | Rows never show owned/capped states |

> **Don't trust a reported `0.5.0` too literally.** The weekly charge cap was
> deployed to Fly.io on 2026-07-13 from an uncommitted working tree while
> `main.py` still read `version="0.5.0"`, so the live service advertised 0.5.0
> for six days while already enforcing the cap. The version string was only
> bumped to 0.6.0 on 2026-07-19. If you need to know what a running instance
> actually supports, probe `/api/state` for the field rather than reading
> `/openapi.json`'s version — that same trap cost a debugging round-trip when
> the exchange-rate callout appeared to be broken client-side but was really an
> undeployed backend.

---

## Plugin 5.16.0 · Backend 0.6.0 — 2026-07-19

### Shop preview images (plugin)

Shop items can carry a picture. New optional `preview_image` field per SKU in
`valcoin_shop.yaml`, accepting either an `https` URL or a path relative to
`BepInEx/config`.

- [`Catalog.cs`](../valheim-plugin/Catalog.cs) — parses `preview_image` into
  `Sku.PreviewImage`. As a plain string field it rides the existing catalog RPC
  to remote clients for free, so a URL set server-side reaches everyone.
- [`ImageCache.cs`](../valheim-plugin/ImageCache.cs) *(new)* — async
  load-once-and-cache keyed by source string. Callers read `Get(source)` every
  OnGUI frame and get `null` until the texture is ready (or forever, on
  failure). `http(s)://` and `file://` pass through; anything else resolves
  against `BepInEx/config` and becomes a `file://` URI.
- [`DonationPanel.cs`](../valheim-plugin/DonationPanel.cs) — 72px thumbnail in
  the shop row, 190px preview in the buy dialog. **Layout space is reserved from
  the SKU field, not from texture readiness**, so a row doesn't jump when an
  async load lands.
- **Build:** added a `UnityEngine.UnityWebRequestTextureModule` reference
  (copied into `libs/` from the Steam install) — `UnityWebRequestTexture` and
  `DownloadHandlerTexture` live there, not in `UnityWebRequestModule`.

### Click-to-enlarge zoom overlay (plugin)

Clicking either preview opens a full-size view: fitted to 80% of the window,
**never upscaled past 1:1** (so a small source stays sharp instead of blurring),
captioned with the SKU name, closing on the Close button, a click outside, or
Escape.

The overlay is drawn **above every other modal** and checked first in `OnGUI`,
so it can be opened from the purchase-confirm dialog and dismissed back to it
with the purchase still staged.

> **Implementation note.** The first version used a full-screen invisible
> `GUI.Button` as the click-outside target. In IMGUI that grabs the mouse before
> any control declared after it, so the Close button never saw a click. Replaced
> with an explicit `MouseDown`-outside-the-panel-rect test ordered *before* the
> panel's own controls.

### Exchange-rate callout (plugin + backend)

The Donate tab leads with a large gold **`$1 USD = N Valcoins`** callout plus a
worked example; the Shop tab carries a compact one-line variant.

- **Backend `/api/state` gained `coins_per_usd`**, read from
  `settings.coins_per_unit["USD"]`. The rate is *not* hard-coded in the plugin —
  a client-side constant, or a value in the per-machine `valcoin_config.json`,
  could drift from what donations actually credit, and `valcoin_config.json`
  isn't synced to clients anyway, so a local fallback would only work on the
  machine that set it.
- If the service is reachable but reports no rate (a backend predating the
  field), the callout reads **"Exchange rate unavailable"** rather than
  rendering nothing. A silently missing rate is indistinguishable from a bug —
  which is exactly how this surfaced in testing.

### Also in this release

Two bodies of work that were **live on the server but never committed** are
included in the 5.16.0 commits:

- **Familiars / Valkyrie carry** — `ArmorVfx.cs` (previously untracked),
  `ValkyrieCarry.cs`, `ShopHandler.cs`, `UiActionRouter.cs`.
- **Weekly charge cap (backend)** — `charge_grants` history table,
  `weekly_charge_cap` on `/api/spend` enforced by summing charges granted this
  week across all SKUs of a kind rather than counting purchases, and
  `weekcap.py` week-boundary helpers.

### Known gap

The live catalog sets `preview_image` as **config-relative paths**
(`shop_images/Bat.png`), and the catalog RPC syncs the *string*, not the file. A
connecting player resolves that against their own `BepInEx/config`, finds
nothing, and sees blank space. Only machines holding the files render
thumbnails. **Fix:** rehost the 8 PNGs at `https` URLs — and downscale first,
since the sources total ~3.5 MB for images that render at 72px. Tracked in
[STATUS.md](STATUS.md).

---

## Plugin 5.13.0 – 5.15.0 — Familiars

Backend unchanged (0.5.0) except the 5.15-era weekly charge cap, which was
deployed at the time but only committed with 5.16.0 above.

- **5.15.0** — Familiars grant a **small flat attack bonus** (`SE_FamiliarBond`
  via the game's `ModifyAttack`: +2/+3 of the creature's damage type) on top of
  feather fall. Buy dialog **warns before overwriting** a familiar already bound
  to the equipped helmet. **Soulkeeper capped at 10 charges/player/week**, shared
  across the x1/x5/x10 tiers. **Tomb-area creature repel** on Valkyrie landing.
  Gjall drips removed; particle scaling tuned.
- **5.14.0** — Familiar clone fixes: dependency-ordered component strip (no more
  "Can't remove Humanoid" log spam), `flying` animator bool so the Drake
  Hatchling animates, height/particle tuning, spawn/despawn puff. Added
  **feather fall** (the Feather Cape's own `SlowFall`, non-stacking).
- **5.13.0** — The `armor_vfx` category became **Familiars**: 8 miniature flying
  creatures (Bat, Ghost, Deathsquito, Drake Hatchling, Wraith, Volture, Gjall,
  Fallen Valkyrie) hovering at the right shoulder, bound to the equipped helmet
  with a matching name suffix, broadcast via ZDO so other players see them.
  Tier-priced 400–1300c. Visuals are whole-creature clones inside an inactive
  holder, stripped of AI/network/physics.

## Plugin 5.10.0 — `armor_vfx`

New shop effect attaching a cosmetic aura to an equipped armor piece, broadcast
via ZDO. Reworked into Familiars in 5.13.0; 5.11–5.12 iterated on slot binding.

## Plugin 5.7.0 – 5.9.0 · Backend 0.5.0 — Soulkeeper, grouped shop, native skin

- **5.9.0** — Panel restyled to match Valheim's own UI (loads the game's
  `AveriaSerifLibre` font, falling back to the IMGUI default if a future build
  renames it). **Buy** now opens a Yes/Cancel confirmation before any Valcoins
  are spent.
- **5.8.0** — Shop tab **grouped into categories** with one `category_desc`
  blurb per group, replacing one long flat list.
- **5.7.0** — **Soulkeeper Charm**, a death-insurance consumable: on death you
  keep your skills (no drain) and a Valkyrie carries you from the spawn point
  back to your tombstone. Sold as stackable charges.
  **Cosmetic chat perks removed** (`donor_badge`, `chat_title`,
  `companion_flair`, `lordslayer_title`, and `ChatDecoration.cs`) — on a
  dedicated server, chat is routed peer-to-peer with no `NetworkUserId`, so
  per-player chat decoration was never reliably renderable. This was an
  architectural dead end, not a bug that was left unfixed.
  - **Backend:** `charges` table, `grant_charges` on `/api/spend`,
    `/api/charges/consume`, and `charges` + `owned_skus` + `weekly_usage` on
    `/api/state` (the client was previously blind to server-side ownership).

## Plugin 5.1.0 – 5.3.0 — UI-only donations

- **5.3.0** — F4 "Codex" and F8 quick panel **merged into one panel on F4**.
  Reworked Donate tab: the code appears inline under the button with Copy and
  Open-portal actions. 30s anti-spam cooldown with live countdown. Terms of Use
  modal. **All emoji replaced with plain text** — Valheim's IMGUI font renders
  them as blank squares.
- **5.2.0** — **All chat and console commands removed** (`/donate`, `/coins`,
  `/shop`, `/buy`, `/gift`, `/topdonors`, `/title`, `/givecoins`,
  `/removecoins`); the chat hook was unreliable alongside other chat-patching
  mods. **Breaking: the plugin is now required client-side** — vanilla clients
  can no longer donate, shop, or gift. New **Admin tab** replaces the removed
  admin commands.
- **5.1.0** — **Catalog syncs to remote clients over RPC** (every 30s);
  previously `valcoin_shop.yaml` existed only on the machine that loaded it, so
  remote clients saw an empty shop. F8 panel tracks live backend reachability
  from real fetch success/failure rather than "config has a URL".

## Plugin 5.0.0 — Initial public release

Valcoin economy over chat commands; four independently-optional providers
(Ko-fi, PayPal, Patreon, PayMongo), each verified by its own webhook signature;
F4 Codex and F8 quick panel; `grant_item` weekly-capped, boss-gated consumables.

## Backend 0.5.0 and earlier

Restructured into a FastAPI app package (`fb6c4c8`), then branded donation
portal, Patreon one-time-link step made explicit, PayPal wired for auto-credit
via `PAYPAL_BUSINESS_EMAIL`, PayMongo portal card fix. All four providers are
live — see [PROVIDERS.md](PROVIDERS.md) and [STATUS.md](STATUS.md).
