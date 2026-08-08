# In-Game UI & Shop

The user-facing surface of the plugin — what players actually see and click.
For the underlying code, see [PLUGIN.md](PLUGIN.md).

> **Status legend:** ✅ built today · 🔜 proposed (design locked, not yet coded —
> see [ecosystem/donation-hooks.md](ecosystem/donation-hooks.md) and
> [../valheim-plugin/examples/valcoin_shop.example.yaml](../valheim-plugin/examples/valcoin_shop.example.yaml)).

## No chat or console commands

**All donation actions go through the in-game panel (open it with F4) —
there is no chat-typed or console command path.** This was a deliberate removal (see
[STATUS.md](STATUS.md)): the reflection-based `Chat.RPC_ChatMessage` hook
proved unreliable on a server running several other mods that also patch
chat, and the UI panels already covered the same actions over a silent RPC.

**Consequence:** a truly vanilla (un-modded) client can no longer use the
donation system at all — the plugin must be installed **client-side**, not
just server-side, to donate/shop/gift. On a ServerGuard-locked server this is
moot (ServerGuard already kicks vanilla clients), but it's a real requirement
change for anyone deploying this mod standalone. See
[PLUGIN.md](PLUGIN.md#vanilla-client-compatibility).

A lightweight `ChatDecorationPatch` still runs server-side — it only prefixes
a player's normal chat messages with their donor badge (⭐) / chat title, if
they own those perks. It doesn't parse or intercept commands.

## The in-game panel (F4) ✅

There is **one** donation panel, opened with **F4** (configurable via
`codex_toggle_key`), built as
[DonationPanel.cs](../valheim-plugin/DonationPanel.cs). It is
**fully navigable offline** (before the backend exists): the shop catalog and
owned perks render from local data, while balance / live patron board /
purchasing show an "activates when online" state and light up automatically
once the operator connects the backend — no client update needed.

```
┌─── Valheim Donations ──────  Live  [X] ┐
│ Balance: 1500 Valcoins                 │
│ [Donate] [Shop] [Gift] [Patrons] [Admin]│  <- Admin only shows for admins
│ ────────────────────────────────────── │
│ < per-tab content >                    │
└────────────────────────────────────────┘
```

- **Donate tab** — clear step-by-step instructions, a high-contrast
  "Get my donation code" button (30s anti-spam cooldown), the code shown
  **inline** with a **Copy code** button and an **Open donation portal** button
  (launches the OS default browser via `Application.OpenURL`), and a
  **Terms of Use** link that opens an in-game modal.
- **Shop tab** — scrollable SKU list with "Buy" buttons; shows owned/charges and
  (for `grant_item` SKUs) the weekly cap + boss gate per row.
- **Gift tab** — recipient + amount fields, "Send gift" button.
- **Patrons tab** — leaderboard of lifetime donors.
- **Admin tab** (admins only) — give/remove a player's Valcoin balance
  manually. Only appears after the server confirms (via a `whoami` RPC
  round-trip) that the local Steam64 is in `valcoin_admins.yaml`.

The panel auto-closes when you open inventory, map, or pause menu. Sends
every action via a silent `vc_action` RPC so nothing appears in public chat.
The plugin must be installed client-side to see this panel at all — see the
[No chat or console commands](#no-chat-or-console-commands) section above.

> **No emoji in the UI.** Valheim's IMGUI font renders emoji as blank squares,
> so the panel and every server reply string use plain text only.

## Shop catalog

SKUs live in `BepInEx/config/valcoin_shop.yaml` (auto-generated on first run).
A fully-worked proposed catalog is in
[../valheim-plugin/examples/valcoin_shop.example.yaml](../valheim-plugin/examples/valcoin_shop.example.yaml).

Each SKU has:

```yaml
shop:
  food_t2:                        # grant_item — weekly-limited consumable
    name: "Plains Feast (bundle)"
    description: "Lox Meat Pie + Bread + Fish Wraps, x5 each. Requires Yagluth."
    price: 180
    effect: grant_item            # grant_perk | add_charges | grant_item
    item: "LoxPie:5,Bread:5,FishWraps:5"   # comma list of prefab or prefab:qty
    weekly_cap: 3                 # max purchases per player per week (0 = unlimited)
    requires_boss: defeated_goblinking     # global boss key gate (optional)
```

The `grant_perk` and `add_charges` effects are still supported by the plugin,
but no perk-based SKUs ship by default (the donor-badge / chat-title cosmetics
were removed — see "Built-in perk handlers" below).

### Fields

| Field | Used by | Meaning |
|---|---|---|
| `name` / `price` | all | Display label + Valcoin cost |
| `description` | all (optional) | Short tag shown after the name, e.g. `Best value` (no longer a full sentence) |
| `effect` | all | `grant_perk` \| `add_charges` \| `grant_item` |
| `perk` | grant_perk / add_charges | Perk id / charge-pool key |
| `charges` | add_charges | Uses granted per purchase |
| `item` | grant_item | Comma list of `prefab` or `prefab:qty` to spawn |
| `weekly_cap` | grant_item | Max purchases per player per week (0 = unlimited) |
| `requires_boss` | grant_item | Global boss key that must be set before buyable |
| `category` | all | Shop grouping label — SKUs sharing it render under one header |
| `category_desc` | all (optional) | One-line blurb for the category; set **once** on the first SKU of each group |
| `preview_image` | all (optional) | Thumbnail shown next to the SKU (Shop tab) and in the purchase-confirm dialog — an `https` URL or a path relative to `BepInEx/config` (e.g. `shop_images/foo.png`) |

**Grouped Shop tab:** the panel groups SKUs by `category` (in first-appearance
order), drawing one header + one `category_desc` blurb per group, then a compact
row per item (name · price · action) with a state note (owned / locked /
"N/week left" / charges held). For `grant_item` SKUs a dim **contents** line is
auto-derived from `item` (e.g. `Lox Pie x5, Bread x5, Fish Wraps x5`) — so
dropping per-item prose doesn't hide what a bundle contains. The shipped catalog
groups into **Soulkeeper Charms**, **Feasts**, **Meads**, and **Supplies**. SKUs
with no `category` fall into a trailing **More** group.

**Preview images:** a SKU with `preview_image` set shows a 72px thumbnail at the
left of its Shop row and a 190px centered preview in the purchase-confirm
dialog. **Clicking either opens a full-size zoom overlay** — the image is fitted
to 80% of the window, never upscaled past 1:1, and closes on the Close button, a
click outside it, or Escape. The overlay draws above every other modal, so it can
be opened from the confirm dialog and dismissed back to it. Images load lazily
and are cached (see
[valheim-plugin/ImageCache.cs](../valheim-plugin/ImageCache.cs)); the row
reserves space up front so it doesn't jump when the image arrives. Because the
catalog is synced to clients over RPC, an `https` URL set server-side reaches
everyone, whereas a config-relative path only resolves on machines that hold the
file — prefer a URL on a dedicated server.

**Exchange-rate note:** the Donate tab leads with a large gold callout —
**`$1 USD  =  50 Valcoins`** — plus a caption giving a worked example ("a $5
donation credits about 250 Valcoins"). The Shop tab carries the same rate as a
compact one-line note. The rate is served by the backend
(`coins_per_unit["USD"]` in [`config.py`](../backend/app/config.py), surfaced via
`/api/state` as `coins_per_usd`) rather than hard-coded in the plugin, so it can
never drift from what donations actually credit. If the service is reachable but
reports no rate (a backend predating `coins_per_usd`), the callout reads
"Exchange rate unavailable" instead of silently rendering nothing — a missing
rate should never be indistinguishable from a bug.

**Native skin + purchase confirm (plugin 5.9.0):** the panel loads Valheim's own
`AveriaSerifLibre` font (found among the loaded `Font`s, falling back to the
IMGUI default if a future build renames it) and uses bronze-framed dark-wood
panel/button textures so it reads as part of the game. Clicking **Buy** no longer
spends immediately — it opens a **Yes / Cancel confirm modal** (the spend RPC
only fires on Yes; the panel behind is disabled + dimmed while it's open). For
`add_charges` (Soulkeeper) purchases the modal and the success message both note
that charges are credited server-side and may take a few seconds to appear.

### Built-in perk handlers

No `grant_perk` perks ship by default. The donor-badge (⭐ chat prefix) and
chat-title cosmetics were removed: on a dedicated server chat is routed
peer-to-peer, so reliable per-player decoration would need a server→client perk
registry plus fragile cross-mod patch ordering. Donations now reward consumables
and convenience instead. The `grant_perk` effect handler remains available for
future cosmetic perks.

### `add_charges` effect — Soulkeeper Charm (death insurance) ✅

A charge-based convenience consumable. Buying an `add_charges` SKU credits a
**backend-tracked charge pool** (`grant_charges` + `charge_kind` on `/api/spend`,
stored in the `charges` table). The shipped `soulkeeper` pool: on the local
player's death, one charge is consumed and the vanilla skill drain is skipped —
you keep your skills. It's **PvE-safe** (activates only *after* you've died, so
it never helps win a fight, even a duel).

- Charges are the player's own state, so the client caches its count (from
  `/api/state.charges`) and the synchronous death path (`Player.OnDeath` +
  `Skills.LowerAllSkills` patches) decides instantly, then decrements via
  `/api/charges/consume` — reconciling on the next poll (`SoulkeeperPoller`).
- Three tiers (`soulkeeper_1/5/10`, 300 / 1200 / 1300) use the **decoy effect** —
  the ×5 exists to make ×10 the obvious buy.
- **Weekly charge cap (v5.15): 10 `soulkeeper` charges per player per week**,
  **shared across all three tiers** (a `weekly_charge_cap: 10` field on each
  SKU). The backend counts it from a new `charge_grants` history table (summing
  `count` within the current week, Monday 00:00 UTC), so it's a budget of
  *charges*, not purchases — one ×10 exhausts the week. A purchase that would
  exceed the cap is **rejected whole** (`429`, no coins taken, "resets in Nd
  Nh"), surfaced in the failed-purchase modal. Legacy plugin builds that don't
  send the field stay unlimited.
- **Phase 2 — the Valkyrie carry (`ValkyrieCarry.cs`, prototype):** the *same*
  warded death also arms a carry. The death position (= where the tombstone
  drops) is captured in `Player.OnDeath`. After respawning at the spawn point
  the player is told "1 charge consumed — a Valkyrie will carry you to your
  tombstone in 20 seconds"; at pickup the screen **fades to black**, the intro
  Valkyrie grabs them *at the spawn point*, and the fade lifts mid-flight on
  the **real spawn→tombstone route** (the vanilla `Valkyrie` is spawned via
  `SetIntro(true)` + private `SpawnValkyrie()`, then its private flight fields
  are re-routed; speed scales with distance, ~35 s flights). During the flight
  the **ESC menu is suppressed** (`Menu.Show` prefix) and **auto-pickup is
  disabled** (items unloading beneath a fast fly-through NRE'd `AutoPickup` in
  testing), both restored on landing. Gated by `valkyrie_carry_visual` (config,
  default on); a watchdog polls `Player.InIntro()` (vanilla
  `Valkyrie.DropPlayer` clears it) with a distance-scaled hard cap — a stall
  degrades to a plain distant teleport to the grave, never a soft-lock.
- **Tomb repel (v5.15):** on landing at the tombstone (both the flight and the
  fallback-teleport paths), three shockwave pulses over ~4.5 s stagger and
  shove every hostile creature within 12 m radially away, so the player isn't
  mobbed the instant control returns. Each pulse applies a **zero-damage,
  no-attacker** `HitData` carrying only stagger + push — verified in
  decompiled `RPC_Damage` that this applies pushback without aggro/aggravation,
  and `Character.Damage()` routes to whichever peer owns the creature. Players,
  tamed animals, and bosses are excluded. It clears the landing; it is **not** a
  lasting safe zone (creatures can wander back).

### `armor_vfx` effect — Familiars (mini flying-creature companions) ✅

Binds a **miniature flying creature** to the buyer's equipped **helmet** — it
hovers at the player's right shoulder, head height (`ArmorVfx.CompanionOffset`,
parented to the player root so it doesn't swing with head turns), and the
helmet gains a matching name suffix. Grew out of a happy accident: the Ghost
"glow" clone looked like a mini ghost pet, so the whole category pivoted. See
[ArmorVfx.cs](../valheim-plugin/ArmorVfx.cs).

Each familiar grants two small perks while its helmet is equipped (v5.15):

- **Feather fall** — the exact `SlowFall` `StatusEffect` the **Feather Cape**
  equips, pulled off the `CapeFeather` item asset. Because it's the same
  effect, wearing the cape *and* a familiar helmet doesn't stack (one icon);
  removing the helmet leaves it alone if the cape is still on.
- **A tiny flat attack bonus** — a real `StatusEffect` (`SE_FamiliarBond`)
  hooking the game's own `ModifyAttack`, so it applies to melee, bows, and
  magic alike, swaps when you change familiars, and re-applies after death.
  The numbers are 1–4 % of endgame weapon damage — flavor, not power; it stays
  inside the "never helps you win a fight" balance rule.

Eight familiars (category **Familiars**, priced by progression tier). The Ghost
keeps the original particle-child + green point-light build; the other seven
clone the creature prefab's **whole visual**: instantiated inside an *inactive
holder* (so Awake never runs on AI/network/physics components), then everything
except renderers / particles / animator / LODs / lights is `DestroyImmediate`d
in **dependency order** (a pass skips any component another still
`[RequireComponent]`s, e.g. `CharacterDrop`→`Humanoid`, so Unity never logs
"Can't remove X"). Nothing real ever spawns (no ZNetView/ZDO, no Character, no
colliders). Flying creatures need the `flying` animator bool set by hand (we
stripped `Character`), so `TuneAnimators` sets it — otherwise the Hatchling
freezes. A **spawn/despawn poof** (`vfx_spawn_small`) plays when a familiar
appears (helmet equipped / comes into view) or vanishes (unequipped).

| SKU | `perk` | Creature prefab | Price | Attack bonus | Suffix |
|---|---|---|---|---|---|
| `familiar_bat` | `bat` | `Bat` | 400 | +2 slash | *…of the Bat* |
| `familiar_ghost` | `ghostlight` | `Ghost` (particle child) | 500 | +2 slash | *…of the Ghost* |
| `familiar_deathsquito` | `deathsquito` | `Deathsquito` | 600 | +2 pierce | *…of the Deathsquito* |
| `familiar_hatchling` | `hatchling` | `Hatchling` | 700 | +2 frost | *…of the Drake* |
| `familiar_wraith` | `wraith` | `Wraith` | 800 | +2 slash | *…of the Wraith* |
| `familiar_volture` | `volture` | `Volture` | 900 | +3 pierce | *…of the Volture* |
| `familiar_gjall` | `gjall` | `Gjall` (0.08×, drips stripped) | 1100 | +2 blunt, +1 fire | *…of the Gjall* |
| `familiar_valkyrie` | `fallen_valkyrie` | `FallenValkyrie` (0.15×) | 1300 | +2 spirit | *…of the Valkyrie* |

The **Gjall's tar drips** are destroyed on spawn (`StripChildHints`), and both
the Gjall and Fallen Valkyrie shrink their particle emission (start size /
speed / gravity / shape radius, each × the body scale) so the drips and smoke
match the mini-pet body instead of dwarfing it.

Flow: **Buy → confirm modal** (states it binds to your helmet, lists feather
fall + the attack bonus, and **warns if the equipped helmet already has a
familiar** — buying overwrites it, only on the Yes button) → the buyer's client
gets an `__ARMORVFX__:<aura>:head` control message and applies it locally
(helmet must be equipped).

- **Source of truth = the item.** The aura is stamped on the equipped item via
  `ItemData.m_customData["vc_armor_vfx"]`, so it persists across relogs and
  drives the rename (a `GetTooltip` postfix prepends the suffixed name —
  per-instance, so only the enchanted piece renames). Requires that piece to be
  equipped.
- **Survives upgrades (5.17.0).** Upgrading the helmet at a forge keeps the
  familiar. This needs explicit handling: Valheim doesn't upgrade a piece in
  place — `InventoryGui.DoCrafting` unequips and removes the old item, then
  mints a **new `ItemData`** in the same grid slot, copying neither
  `m_customData` nor `m_equipped`. `ArmorVfxUpgradePatch` re-stamps the
  replacement (matched by shared name + expected quality, so an aborted craft
  can't be mis-stamped) and re-equips it, since vanilla otherwise leaves the
  upgraded piece sitting unequipped in the inventory.
- **The Upgrade view names the familiar (5.17.0).** Selecting a familiar-bearing
  piece in the crafting panel shows *"Bronze Helmet of the Bat"*, the familiar
  and its attack bonus, and a *"Kept when this piece is upgraded."* note — so
  it's obvious before the materials are spent. The panel builds its description
  from the **recipe prefab**, which carries no `m_customData`, so the normal
  rename never reaches it; `ArmorVfxUpgradePanelPatch` reads the player's actual
  selected piece instead.
- **Others see it too.** The local player mirrors "aura per equipped slot" onto
  their own **Player ZDO** (`vc_vfx_head/chest/legs`), which replicates to every
  client. `ArmorVfxManager` (client-only, ~0.75 s tick) reads every visible
  player's ZDO and attaches/detaches the particle prefab on the matching body
  part (`VisEquipment` instance / bone), re-attaching after re-equips.
- **Prototype caveats:** prefabs resolve via `ZNetScene.GetPrefab` then a cached
  `Resources` scan; instantiated copies are stripped of `ZNetView` /
  `TimedDestruction` etc. so they persist as pure local visuals. If a prefab
  can't be found or still self-destructs on some build, the **purchase + rename
  still succeed** and it logs under `[Valcoin][ArmorVfx]` — the visual degrades,
  never crashes. Needs in-game verification (prefab resolution + loop behavior).

### `grant_item` effect (weekly-limited consumables) 🔜

Sells **hard-to-cook food, meads, and grind-heavy earnable materials** — capped
per player per week so donations top up a supply, never replace playing. Balance
guardrails (see [ecosystem/donation-hooks.md](ecosystem/donation-hooks.md)):

- **Weekly cap** per player per SKU, reset on a fixed boundary (recommend Monday
  00:00 server time). Enforced **backend-side** on `/api/spend` (it owns the
  ledger); the Shop tab reports "cap reached, resets in Nd Nh".
- **Progression gate** via `requires_boss` — e.g. Ashlands food needs
  `defeated_fader`, so you can't buy end-game food as a Meadows newbie.
- **Earnable only** — everything sold is something a player can already
  make/farm; nothing exclusive. Sold in small x5 bundles, not stacks of 50.
- **Not sold** (would skip core mining/exploration): raw ore & bars (Iron,
  Silver, Black metal, Flametal), Surtling cores, Refined Eitr, Chitin, Ancient
  seeds, Dragon tears.

Shipped default catalog (9 weekly-limited `grant_item` bundles):

| SKU | Gate | Bundle | Price | Weekly cap |
|---|---|---|---|---|
| `food_t1` — Sausages, Blood Pudding, Serpent Stew | `defeated_bonemass` | x5 ea | 120 | 4 |
| `food_t2` — Lox Meat Pie, Bread, Fish Wraps | `defeated_goblinking` | x5 ea | 180 | 3 |
| `food_t3` — Misthare Supreme, Mushroom Omelette, Yggdrasil Porridge | `defeated_queen` | x5 ea | 260 | 2 |
| `food_t4` — Mashed Meat, Piquant Pie, Marinated Greens | `defeated_fader` | x5 ea | 350 | 2 |
| `meads_utility` — Tasty, Frost Res, Poison Res | — | x5 ea | 100 | 3 |
| `meads_vitality` — Medium Healing, Medium Stamina | `defeated_bonemass` | x5 ea | 160 | 2 |
| `meads_eitr` — Minor Eitr | `defeated_queen` | x5 | 160 | 2 |
| `farm_bundle` — Barley, Flax, Onion/Carrot/Turnip seeds | `defeated_goblinking` | x20 ea | 120 | 2 |
| `forage_bundle` — Coal, Resin, Feathers, Thistle, Dandelion, Honey | — | x50/x20 | 100 | 2 |

The `grant_item` effect + weekly cap + `requires_boss` gate are **implemented and
live** (`Catalog.cs`, `ShopHandler.cs`, `/api/spend`) — see [PLUGIN.md](PLUGIN.md)
and [BACKEND.md](BACKEND.md). All 20 SKUs (3 Soulkeeper Charm charge tiers +
8 Familiars + 9 `grant_item` bundles) ship as the auto-generated default
`valcoin_shop.yaml`, so a fresh install gets the full catalog. **A server that
already generated the old 3-SKU `valcoin_shop.yaml` will NOT auto-overwrite it**
(`EnsureFile` skips when the file exists) — replace that YAML on the host and
restart to promote the catalog on an existing server. Adding or editing a SKU is
just a YAML edit + restart.

## Earning Valcoins by playing (ServerGuide quests)

Coins normally arrive by donating. Quests are the other tap: a **one-time
"Patron's Welcome"** worth **30 Valcoins** that teaches the donation system, and
a **daily pool capped at 8/day** with a 7-day streak bonus (+20), so there's a
reason to log in.

**ServerGuide is not modified.** It has no currency reward type, so each quest
sets a player key `VC.Q.<id>` using its stock `set_player_key` reward; the
plugin watches for the key and the backend decides whether it pays.

Two config files, and they must agree:

| File | Lives in | Holds |
|---|---|---|
| `guidance.valcoin-quests.yaml` | server's `BepInEx/config/ValheimServerGuide/` | the quests — triggers, text, and the `VC.Q.<id>` key each sets |
| `valcoin_quests.yaml` | server's `BepInEx/config/` | what each `<id>` is worth (`coins`, `period: once\|daily`) |

A key with no matching entry in `valcoin_quests.yaml` is worth nothing and logs
`Unknown quest '<id>'`. Drafts of both ship in
[../valheim-plugin/examples/guidance.valcoin-quests.yaml](../valheim-plugin/examples/guidance.valcoin-quests.yaml)
and as the self-written template in
[../valheim-plugin/QuestCatalog.cs](../valheim-plugin/QuestCatalog.cs).

**The daily pool sums to 20 against a cap of 8 — on purpose.** Nobody clears it;
you do what suits the session. Felling a Biome Lord (8) caps you on its own.

| Quest | Trigger | Coins |
|---|---|---|
| Answer the Horn — be online ~15 min | `timed` | 2 |
| Thin the Wilds — kill 15 common creatures | `kill`, `count: 15` (7 tier variants) | 3 |
| Tend the Beasts — tame anything | `tamed_creature` | 2 |
| Fell a Lord — kill any Biome Lord | `kill` (7 Lord prefabs) | 8 |
| Forge a Bond — recruit/level/duel a Dvergr | Lost Scrolls II triggers | 5 |

Balance notes, and why this doesn't undercut donating:

- **Quest coins never touch the Patron board.** They're granted with
  `source = 'quest'`; the leaderboard sums `source = 'donation'` only.
- **The cap is the throttle.** 8/day is ~56/week — roughly one 100-coin
  consumable every week and a half, against 50 coins for a $1 donation.
- **Reset is 00:00 UTC**, matching ServerGuide's own `real_world_time` /
  `day_of_week` triggers and SQLite's `datetime('now')`.
- **Nothing banks.** A quest finished at the cap pays nothing and doesn't queue
  for tomorrow.

Players check their standing on the F4 panel, under the balance: *"Daily quests:
5/8 · resets in 6h 12m · 4-day streak"*.

**Never promise coins in the ServerGuide text.** A daily's display fires on every
completion, but only the first each day pays, and nothing pays past the cap.
Let the quest text describe what happened in-world and leave coins to the plugin —
it's the only half that knows what was actually paid.

## Advertising the donation system

Defaults-light advertising kit — the goal is to make the donation flow
discoverable without nagging players.

| Approach | Status | Annoyance | Notes |
|---|---|---|---|
| **In-game panel (F4)** | ✅ | none | The browsable, opt-in home for donating, the shop, gifting, and Top Patrons. |
| **One-time HUD on join** | Built, **default ON** | very low | Single TopLeft line, 5s after spawn, points at F8/F4. Toggle via `welcome_message_enabled`; customise via `welcome_message` in `valcoin_config.json`. |
| **Donor ⭐ badge in chat** | Built | none | Pure passive social proof — donors show off by chatting. |
| **Top Patrons leaderboard** | Built | none | Opt-in: open the Top tab (F8) or Patrons section (F4). |
| **Haldor "Support" conversation** | 🔜 (ServerGuide YAML) | none | In-lore hold-E dialogue explaining donations. No new code — pure `guidance.yaml`. |
| **Lord-kill "sponsored by top donor" beat** | 🔜 (ServerGuide YAML) | low | Celebratory global message on a BiomeLord/boss kill. |
| **Gentle `timed` reminder** | 🔜 (ServerGuide YAML) | low | Raven popup ≥ 60 min interval, `stop_when` already-donated. |
| **Unified Discord webhook** | 🔜 | none in-game | Point donations + ServerGuide + ServerGuard webhooks at one channel. |
| **Spawn-area signs** | manual | none | Admin places signs saying "Press F4 for donations" or links a URL. Zero plugin code. |

Most of the ecosystem promos are **pure ServerGuide YAML** (no new code) because
ServerGuard guarantees every player runs the modpack — so they reach 100% of
players. A ready-to-drop draft covering the Haldor conversation, timed reminder,
boss-kill gratitude beats, and first-recruit footnote is in
[../valheim-plugin/examples/guidance.donations.yaml](../valheim-plugin/examples/guidance.donations.yaml).
See [ecosystem/donation-hooks.md](ecosystem/donation-hooks.md) for the full plan.

## Source of truth

- [valheim-plugin/UiActionRouter.cs](../valheim-plugin/UiActionRouter.cs) — server-side dispatch for every panel action (including admin)
- [valheim-plugin/Flows.cs](../valheim-plugin/Flows.cs) — donate/gift/leaderboard implementations, shared by the router
- [valheim-plugin/ShopHandler.cs](../valheim-plugin/ShopHandler.cs) — `ApplyEffect` dispatch (add `grant_item` here)
- [valheim-plugin/Catalog.cs](../valheim-plugin/Catalog.cs) — YAML loader (add `item` / `weekly_cap` / `requires_boss`)
- [valheim-plugin/DonationPanel.cs](../valheim-plugin/DonationPanel.cs) — the single combined panel (F4)
- [valheim-plugin/ChatDecoration.cs](../valheim-plugin/ChatDecoration.cs) — passive badge/title chat prefix (not a command)
- [valheim-plugin/examples/valcoin_shop.example.yaml](../valheim-plugin/examples/valcoin_shop.example.yaml) — proposed catalog
- [valheim-plugin/QuestCatalog.cs](../valheim-plugin/QuestCatalog.cs) — quest id → coins/period loader (`valcoin_quests.yaml`)
- [valheim-plugin/QuestWatcher.cs](../valheim-plugin/QuestWatcher.cs) — client-side `VC.Q.*` key poll
- [valheim-plugin/QuestFlow.cs](../valheim-plugin/QuestFlow.cs) — server-side claim + player messaging
- [backend/app/routes/quests.py](../backend/app/routes/quests.py) — `/api/quests/claim`, the only real payout gate
- [valheim-plugin/examples/guidance.valcoin-quests.yaml](../valheim-plugin/examples/guidance.valcoin-quests.yaml) — the quests themselves (ServerGuide YAML)
