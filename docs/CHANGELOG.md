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
| Plugin | **5.18.0** | [`Plugin.cs`](../valheim-plugin/Plugin.cs) `[BepInPlugin]` 3rd arg |
| Backend | **0.7.1** | [`main.py`](../backend/app/main.py) `FastAPI(version=...)` |
| Thunderstore package | **5.17.0** | [`manifest.json`](../Thunderstore%20files/Valheim_Donations/manifest.json) `version_number` |

> The Thunderstore package is deliberately **one behind** at 5.17.0 — 5.18.0 has
> not been cut as a release. Packaging is its own checklist
> ([THUNDERSTORE.md](THUNDERSTORE.md)) and bumping `manifest.json` is what
> starts it, so the four-places rule below applies at release time, not at
> commit time.

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
| Quest rewards (`/api/quests/claim`) | 0.7.0 | Every completed quest 404s — no coins, warning per completion in the server log |
| Daily-quest panel line (`quest_daily_*` on `/api/state`) | 0.7.0 | Line hidden (cap reads 0, which the panel treats as "quests disabled") |
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

## Backend 0.7.1 — 2026-08-11

**Backend-only; no plugin change.** Fixes a silent money-loser: every
first-time Ko-fi donation was crediting nothing.

### The bug

The portal deep-linked to `ko-fi.com/<user>/?message=<code>` and told the donor
*"Your code is pre-filled into the message field automatically — nothing to
paste."* **Ko-fi has no message prefill.** The query param survives in
`location.search` but Ko-fi never reads it into its message box
(`textarea[name=txtThanks]`), so the webhook arrived with no claim code,
`codes.find_in_text()` returned `None`, and — with no `provider_links` row for a
first-time donor — the donation was filed `unmatched` and paid out nothing.

Donors who followed the instructions exactly got nothing, on the provider the
portal labels **Recommended**. Found after a $10 donation on 2026-08-11
(donation id 4) never landed; credited manually via `/api/admin/credit-unmatched`.

**Why the tests didn't catch it:** every Ko-fi test injected the code straight
into the payload's `message` field, so they all exercised the parser and none
exercised the delivery assumption. The one integration assumption in the chain
was the only untested link in it.

### Changes

- [`portal.py`](../backend/app/routes/portal.py) — Ko-fi deep link drops the
  dead `?message=` param, with a comment recording why so it doesn't come back.
- [`portal_code.html`](../backend/app/templates/portal_code.html) — the Ko-fi
  card now tells donors to paste the code into "Your message" and shows it
  inline, mirroring how the PayPal.Me card already handled the same limitation.
  Card converted from `<a>` to `<div>` so it can hold a form.
- **New `POST /portal/kofi/link`** — self-service rescue for donors who forgot.
  Enter the Ko-fi email, and it binds the provider link, retro-credits, and
  burns the claim code. Surfaced as an "Already donated?" disclosure.
- [`donations.py`](../backend/app/donations.py) — `credit_unmatched_for()` takes
  a `since` bound; provider-link matching is now case-insensitive.

### Two guardrails worth knowing

- **The retro-credit sweep is time-scoped.** The Ko-fi flow passes the claim
  code's mint time as `since`, so linking can only capture donations from *this*
  portal session. Without it, anyone holding a valid code could guess a
  stranger's Ko-fi email and claim their stranded donations.
- **Email casing was a latent second failure.** The auto-link in
  `record_donation()` stored whatever casing Ko-fi echoed but looked it up with
  an exact match, so a donor who typed `Donor@` once and `donor@` the next time
  would have missed their own link. Both sides now compare with `LOWER()`.

### Tests

[`test_kofi_link.py`](../backend/tests/test_kofi_link.py) — 8 new, including a
regression guard asserting the portal never emits `message=` and never uses the
words "prefilled"/"pre-filled". Be clear on its limits: **nothing in CI can
verify Ko-fi's own behavior.** The guard proves we no longer depend on behavior
Ko-fi doesn't have, which is the assumption that actually broke.

---

## Plugin 5.18.0 · Backend 0.7.0 — 2026-08-07

**Paired release — deploy the backend first.** The plugin calls a new endpoint;
against 0.6.0 every quest completion 404s and silently pays nothing.

Adds ServerGuide-driven quest rewards: a one-time onboarding quest worth 30
Valcoins, and a pool of dailies capped at 8/day, to introduce the donation
system and give people a reason to log in.

### How a ServerGuide quest pays coins (the whole mechanism)

ServerGuide has **no currency reward type** — `currency` was sketched in its
roadmap but [CRIT-23](../../Valheim%20ServerGuide/.claude/criteria/CRIT-23-phase5-enhanced-rewards.md)
shipped 13 types without it. Rather than modify ServerGuide, quests signal
completion with its stock `set_player_key` reward and this plugin does the rest:

```
quest completes → ServerGuide sets player key "VC.Q.<id>"
   → QuestWatcher (client, 5s poll) spots it, clears it, sends "quest:<id>"
   → QuestFlow (server) prices it from valcoin_quests.yaml, POSTs /api/quests/claim
   → backend decides whether it pays → grants row (source='quest')
   → existing GrantPoller delivers "+N Valcoins!" and updates CoinManager's cache
```

**ServerGuide itself is unmodified.** It only receives a new YAML file in its
config folder.

- **Clearing the key is what re-arms a daily.** Nothing client-side tracks days;
  the quest simply fires again tomorrow and reports again.
- **Coins ride the existing grant pipeline on purpose.** Messaging the player
  directly would have been one fewer hop but would leave `CoinManager`'s local
  balance cache stale, since that cache is only written by `GrantPoller`.

### Trust model — quest completion is client-attested, and can't not be

Quest progress lives in the client's character `m_customData` (Valheim
characters are client-owned), so the server can only ever be *told* a quest
finished — ServerGuide's own chain-state sync has the same property. The backend
is therefore the only real gate, and the ceiling on a fabricated report is one
day's allowance:

- `UNIQUE(steam64, quest_id, period_key)` on `quest_claims` — `period_key` is the
  UTC date for dailies and the literal `once` for one-time quests, so the same
  constraint enforces both.
- A per-player daily coin cap (`quest_daily_cap`, default 8).
- **The client sends only a quest id, never a value.** Pricing is looked up
  server-side in `valcoin_quests.yaml`, so a hand-crafted RPC can name a quest
  but not price it, and an unknown id is a logged no-op.

### Backend (0.7.0)

- [`schema.sql`](../backend/app/schema.sql) — new `quest_claims` table.
  `counts_toward_cap` is 0 for streak-milestone rows so the bonus lands on top of
  the daily cap instead of being swallowed by it.
- [`quests.py`](../backend/app/quests.py) — day-key/reset/earned/streak helpers.
  Both the claim endpoint (enforcement) and `/api/state` (display) read from
  here, so the number shown can't disagree with the number enforced — same
  reason [`weekcap.py`](../backend/app/weekcap.py) exists for the weekly caps.
- [`routes/quests.py`](../backend/app/routes/quests.py) — `POST /api/quests/claim`,
  returning `credited` | `already_claimed` | `cap_reached` plus the daily
  standing, so the plugin can say something useful in every case.
- **Partial payouts, not refusals.** Finishing a 5-coin quest with 2 of the
  allowance left pays 2. The work was done; paying nothing would read as a bug.
  A claim blocked *entirely* by the cap writes no row, so the quest stays
  claimable rather than being silently spent.
- **One-time quests are cap-exempt.** Otherwise the welcome quest's 30 coins
  would be trimmed to 8 for anyone who'd already done a daily that morning.
- Streak bonus every Nth consecutive day (default 7 → +20), recorded as its own
  claim row keyed on the milestone so several quests on the 7th day pay it once.
- `/api/state` gains `quest_daily_earned` / `_cap` / `_resets_in` / `_streak`
  (additive — older plugins ignore them).
- `POST /api/admin/quest-reset` — clears claims so a daily can be re-tested
  without waiting for 00:00 UTC. Deliberately does **not** claw back granted
  coins; use `/api/admin/grant` for that.
- 19 new tests ([`test_quests.py`](../backend/tests/test_quests.py)); suite is 84 passing.

### Plugin (5.18.0)

- [`QuestCatalog.cs`](../valheim-plugin/QuestCatalog.cs) — loads
  `BepInEx/config/valcoin_quests.yaml` (id → coins/period), same hand-rolled
  parser as `Catalog.cs` and for the same reason: no YamlDotNet dependency.
  Self-writes a populated template on first run.
- [`QuestWatcher.cs`](../valheim-plugin/QuestWatcher.cs) — client-side 5s poll
  for `VC.Q.*` keys. Polling, not hooking: there's no event to hook (ServerGuide
  sets the key internally) and a poll self-heals a missed tick.
- **The key is cleared on server ack, not on send** (`vc_questack`). The first
  cut cleared it immediately, reasoning that ZRoutedRpc is reliable — which
  conflates *delivered* with *understood*. First live test proved the
  difference: the dedicated server was still on 5.17.0, whose `UiActionRouter`
  has no `quest` case, so it answered `Unknown UI action: quest` while the
  client had already dropped the key. Because `vc_welcome_haldor` is
  `once: true` and had fired, the completion was **unrecoverable, not merely
  unpaid** — upgrading the server afterwards couldn't fix it. The same hole was
  open for a backend outage or an unpriced quest. Now the ack is sent only on a
  definitive backend answer; anything else leaves the key and re-reports every
  60s, so a payout is postponed rather than destroyed.
- **RPC registration fixed for listen servers.** Only one side was registered,
  chosen by `IsServer()`, so a host registered the server half and never the
  client half — no panel messages, no catalog, and no way to receive a quest
  ack (its own player would have re-reported forever). A host is both; only a
  dedicated server has no client half.
- [`QuestFlow.cs`](../valheim-plugin/QuestFlow.cs) — server-side claim + the
  player-facing messaging.
- [`RpcLayer.cs`](../valheim-plugin/RpcLayer.cs) / [`CatalogSync.cs`](../valheim-plugin/CatalogSync.cs)
  — new `vc_quests` broadcast on the existing 30s catalog loop. Without it a
  remote client has no idea which keys to watch, since the YAML is server-only.
- [`DonationPanel.cs`](../valheim-plugin/DonationPanel.cs) — "Daily quests: 5/8 ·
  resets in 6h 12m · 4-day streak" under the balance, on every tab.

### Telling the player what happened

A capped player keeps finishing quests and getting nothing, which reads as a bug
unless it's explained. Three layers:

1. **Passive** — the F4 status line above. The surface a player checks *before*
   asking why a quest didn't pay.
2. **On claim** — the panel log names the quest and the payout immediately;
   `GrantPoller` shows the `+N Valcoins!` toast within its 10s poll. Split
   across two surfaces so neither duplicates the other.
3. **Anti-spam** — the "cap reached" toast fires at most once per UTC day per
   player (keyed by date, not session, so a relog doesn't re-nag). The panel log
   still records every occurrence.

**No banking.** A quest finished while capped pays nothing and doesn't queue for
tomorrow — that would let someone farm a weekend into a fortnight of payouts,
defeating the daily-login goal the feature exists for.

### Quest content — [`guidance.valcoin-quests.yaml`](../valheim-plugin/examples/guidance.valcoin-quests.yaml)

21 ServerGuide entries. Deploy to `BepInEx/config/ValheimServerGuide/` on the
**server** (it owns the YAML and syncs it to clients).

**No `first_login` anywhere, deliberately.** `first_login`, `chest_opened` and
`location_entered` each burn a one-shot per-character dedup key on first fire —
which, for anyone already playing here or importing a local-seed character, was
months ago. They would silently never fire for exactly the established players
this content most needs to reach. The welcome quest opens on
`crafting_table_used` instead. (Lost Scrolls II hit the same trap and chose
`distance` over `location_entered` for the same reason.)

Three more ServerGuide constraints the authoring works around, each of which
fails *silently* — the loader uses `IgnoreUnmatchedProperties()`:

- **`GuidanceStep` has no `Conversation`, `HoverText` or `Rewards`.** All three
  are entry-level only. So the welcome quest is a 2-step chain plus a separate
  Haldor entry gated with `requires:` — which works because
  `PrerequisiteChecker` checks `ChainState.IsComplete` before
  `SeenTracker.HasFired`, so a completed chain satisfies a prerequisite.
- **Node-based conversations never grant rewards.** `OnNodeConversationEnd`
  marks the entry fired but never calls `RewardDispatcher`; only
  `ChoiceSpec.Rewards` (the flat `choices:` list) is ever granted. A `nodes:`
  tree here would have looked right and paid nothing.
- **Selecting *any* choice marks a `once: true` entry fired.** So both Haldor
  choices carry the reward. A "Never mind" option without one would let a player
  burn their single 30-coin payout on a mistap.

Content: `kill` is the only trigger with a true cumulative counter (it clears
itself on completion, which is what makes a kill quest repeatable), so it
carries the dailies that should take real effort. `item_acquired`'s `count` reads
*current inventory*, so a "gather 20 Wood" daily would auto-complete for anyone
already holding wood — hence no gathering quests. Multi-tier quests are several
entries sharing one key (`trigger.creature` is one exact prefab, no wildcard),
so an Ashlands player isn't sent hunting Greylings.

### Deploying this one

**The ServerGuide YAML and the plugin DLL must be promoted together.** The quest
lives on the server and syncs to clients; the payout lives in the plugin. Ship
the YAML alone and the quest fires perfectly while the reward silently doesn't —
which is exactly how the first live test failed. The dedicated server is not a
`deploy.ps1` target and its plugin folder needs an elevated shell, so it's easy
to do by halves. Diagnosis shortcut: **the server log must show
`[Valcoin] Quest catalog loaded: N quest(s)`** — that line is 5.18.0+ only, and
its absence is the whole answer. Full runbook in [OPERATIONS.md](OPERATIONS.md).

**Verified working in-game 2026-08-08** on the dedicated server: quests fire,
coins credit, daily cap and F4 status line behave.

---

## Plugin 5.17.0 · Backend 0.6.0 (unchanged) — 2026-08-07

Plugin-only release. No backend change, no new API surface — nothing here
touches the coin ledger, so any backend ≥ 0.6.0 is fine.

### Familiars survive an armor upgrade (plugin) — bug fix

A familiar was lost the moment you upgraded the helmet carrying it. The aura is
stamped on the **item instance** (`ItemData.m_customData["vc_armor_vfx"]`), and
`InventoryGui.DoCrafting` does **not** upgrade a piece in place: it records the
old piece's grid slot, unequips it, `RemoveItem`s it, then mints a **brand-new
`ItemData`** via `Inventory.AddItem(...)` in that slot. That overload copies
neither `m_customData` nor `m_equipped`, so the aura rode off on the discarded
instance and the familiar vanished — permanently, since nothing re-stamped it.

- [`ArmorVfx.cs`](../valheim-plugin/ArmorVfx.cs) — new `ArmorVfxUpgradePatch`
  wrapping `DoCrafting`. **Prefix** remembers the aura, the grid slot, the
  shared name, the expected next quality, and whether the piece was worn.
  **Postfix** finds the freshly minted replacement and re-stamps it.
- **The replacement is matched by shape, not just position** — same shared name,
  quality exactly one higher, and no aura key yet. An aborted craft leaves the
  *original* (already stamped, still at the old quality) in that slot, so it can
  never be mistaken for the upgrade. If the slot moved out from under us, it
  falls back to scanning the inventory for the same shape.
- **It also re-equips the piece.** Vanilla drops the upgraded item into the
  inventory *unequipped* — `InventoryGui` never re-equips after crafting (its
  only `EquipItem` calls are in the drag/drop path). Preserving `m_customData`
  alone would have left the familiar gone until the player manually re-equipped
  the helmet. This is a deliberate, scoped divergence from vanilla: it fires
  **only** for a piece that was equipped *and* carries a familiar.
  `Humanoid.EquipItem` no-ops on an already-equipped item, so it's safe either way.

### Familiar shown on the crafting panel's Upgrade view (plugin)

The Upgrade view now says which familiar the piece carries, so you can see it
before spending the materials.

- [`ArmorVfx.cs`](../valheim-plugin/ArmorVfx.cs) — new
  `ArmorVfxUpgradePanelPatch` on `InventoryGui.UpdateRecipe`. The description
  gains a gold *"Bronze Helmet of the Bat"* header, the familiar's name and
  attack bonus, and a *"Kept when this piece is upgraded."* line; the
  *"Upgrade … to level N"* header carries the suffix inline.
- **Why a second patch was needed:** the panel builds its description from the
  **recipe prefab's** `ItemData`
  (`GetTooltip(m_selectedRecipe.Recipe.m_item.m_itemData, …)`), which has no
  `m_customData` — so the `GetTooltip` postfix that renames the item everywhere
  else never fires here. This patch reads the **player's** selected piece
  (`m_selectedRecipe.ItemData`) instead.
- `UpdateRecipe` reassigns both labels every frame, so prepending can't
  accumulate; a zero-width-space marker guards against it regardless.
- **`SetupUpgradeItem` is dead code** in this Valheim build — it sets exactly
  the labels we want but has no call sites. Patching it would have silently done
  nothing; `UpdateRecipe` is the live path.

Both patches are `Prepare()`-guarded like the existing tooltip patch: if a
Valheim build doesn't resolve the target method or fields, the patch skips
itself and logs, rather than aborting the whole `PatchAll` and taking every
other patch down with it. `Vector2i` (grid position), `TMP_Text`, and the
private `RecipeDataPair` struct are all reached by reflection, so no new
assembly references were added to the csproj.

### Deploy script now targets the test profile only — and fails loudly

`deploy.ps1` deployed to three destinations and **warned-but-continued** on a
missing folder. The played profile has been renamed repeatedly
(`Hearthbound Valheim` → `Hearthbound Server` → `HB Server`), so deploys
silently skipped it while still reporting success, leaving it on weeks-old DLLs.

**This cost two debugging sessions.** The familiar-upgrade fix above was
reported "still broken" after testing, when in fact the profile under test was
running a three-week-old DLL that predated the fix entirely.

- [`deploy.ps1`](../valheim-plugin/deploy.ps1) — deploys to **one** destination,
  the `Hearthbound Valheim - Test` profile (owner's instruction, 2026-08-07).
  The played profile and the dedicated server are deliberately **not** targets;
  promoting a verified build to them is a manual step.
- A missing target folder now **throws**. With a single destination a skip means
  nothing was deployed at all, so it must be loud.
- See [OPERATIONS.md](OPERATIONS.md). **Before concluding an in-game fix didn't
  work, compare the deployed DLL's timestamp and size against
  `valheim-plugin/bin/Release/`.**

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
