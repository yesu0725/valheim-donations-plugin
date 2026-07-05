# Chat Commands & Shop

The user-facing surface of the plugin — what players actually type and see.
For the underlying code, see [PLUGIN.md](PLUGIN.md).

> **Status legend:** ✅ built today · 🔜 proposed (design locked, not yet coded —
> see [ecosystem/donation-hooks.md](ecosystem/donation-hooks.md) and
> [../valheim-plugin/examples/valcoin_shop.example.yaml](../valheim-plugin/examples/valcoin_shop.example.yaml)).

## Chat commands

| Command | Who | What | Status |
|---------|-----|------|--------|
| `/coins` | anyone | Show your Valcoin balance + owned perks | ✅ |
| `/donate` | anyone | Mint a claim code + DM you the donation URL | ✅ |
| `/shop` | anyone | List all SKUs + your balance + ownership | ✅ |
| `/buy <sku>` | anyone | Purchase a SKU | ✅ |
| `/gift <player> <amount>` | anyone | Transfer Valcoins to another player | ✅ |
| `/title <text \| clear>` | perk | Set chat title prefix (needs `chat_title` perk) | ✅ |
| `/topdonors` | anyone | Show lifetime top 5 donor leaderboard | ✅ |
| `/givecoins <player> <amount>` | admin | Grant coins manually | ✅ |
| `/removecoins <player> <amount>` | admin | Subtract coins manually | ✅ |

Admins are listed in `BepInEx/config/valcoin_admins.yaml` (Steam64 IDs only).

> **Removed by design decision:** `/sethome`, `/home`, and `/shout` (with their
> `sethome` and `shout` perks). They still exist in code until the next plugin
> update — to retire them now, delete their SKUs from `valcoin_shop.yaml`; the
> code paths can be pruned in the same pass that adds `grant_item`.

## Donation Codex (F4) ✅ (offline-resilient) · live data 🔜

A dedicated **donations-only** Codex panel — separate from ServerGuide's F3
guide Codex — opened with **F4** (configurable via `codex_toggle_key`). Built as
[DonationCodex.cs](../valheim-plugin/DonationCodex.cs). It is **fully navigable
offline** (before the backend exists): the command reference, shop catalog, and
owned perks render from local data, while balance / live patron board /
purchasing show an "activates when online" state and light up automatically once
the operator connects the backend. Sections — Overview · Perks & Shop · Patrons
· Donate. It is the single home for the whole donation surface, so nothing has
to nag in chat:

- **How it works** — Valcoins, `/donate`, the claim-code flow.
- **Economy & commands** — every supporting command in one place: `/coins`,
  `/shop`, `/buy <sku>`, `/gift <player> <amount>`, `/topdonors`, `/title`, plus
  the F8 quick panel.
- **Perks** — the current cosmetic + consumable catalog with prices and any
  **weekly limits remaining**.
- **Top Patrons** — a leaderboard section mirroring `/topdonors` (lifetime top
  donors), refreshed on open. Passive social proof that lives *in* the Codex,
  not in chat.

Implementation note: this is new plugin UI (reuse the vanilla IMGUI approach of
the F8 panel); everything inside it reads existing backend data (`/shop`,
`/topdonors`, balance).

## Shop catalog

SKUs live in `BepInEx/config/valcoin_shop.yaml` (auto-generated on first run).
A fully-worked proposed catalog is in
[../valheim-plugin/examples/valcoin_shop.example.yaml](../valheim-plugin/examples/valcoin_shop.example.yaml).

Each SKU has:

```yaml
shop:
  donor_badge:                    # ✅ grant_perk — cosmetic flag
    name: "Donor Badge"
    description: "A star next to your name in chat. Forever."
    price: 500
    effect: grant_perk            # grant_perk | add_charges | grant_item
    perk: donor_badge             # internal perk id (grant_perk / add_charges)

  food_t2:                        # 🔜 grant_item — weekly-limited consumable
    name: "Plains Feast (bundle)"
    description: "Lox Meat Pie + Bread + Fish Wraps, x5 each. Requires Yagluth."
    price: 180
    effect: grant_item            # spawns items into inventory (drop if full)
    item: "LoxPie:5,Bread:5,FishWraps:5"   # comma list of prefab or prefab:qty
    weekly_cap: 3                 # max purchases per player per week (0 = unlimited)
    requires_boss: defeated_goblinking     # global boss key gate (optional)
```

### Fields

| Field | Used by | Meaning |
|---|---|---|
| `name` / `description` / `price` | all | Display + Valcoin cost |
| `effect` | all | `grant_perk` \| `add_charges` \| `grant_item` |
| `perk` | grant_perk / add_charges | Perk id the `PerkManager` understands |
| `charges` | add_charges | Uses granted per purchase |
| `item` 🔜 | grant_item | Comma list of `prefab` or `prefab:qty` to spawn |
| `weekly_cap` 🔜 | grant_item | Max purchases per player per week (0 = unlimited) |
| `requires_boss` 🔜 | grant_item | Global boss key that must be set before buyable |

### Built-in perk handlers

| Perk | Type | What it does | Status |
|---|---|---|---|
| `donor_badge` | grant_perk | Adds ⭐ prefix to the player's chat messages | ✅ |
| `chat_title` | grant_perk | Unlocks `/title <name>` to set a `[Bracket]` prefix | ✅ |
| `companion_flair` | grant_perk | Donor-only badge colour / name style on your Lost Scrolls II Dvergr (cosmetic only) | 🔜 |
| `lordslayer_title` | grant_perk | Gilded colour of the *earned* Lordslayer title (must have slain all 7 BiomeLords) | 🔜 |

### `grant_item` effect (weekly-limited consumables) 🔜

Sells **hard-to-cook food, meads, and grind-heavy earnable materials** — capped
per player per week so donations top up a supply, never replace playing. Balance
guardrails (see [ecosystem/donation-hooks.md](ecosystem/donation-hooks.md)):

- **Weekly cap** per player per SKU, reset on a fixed boundary (recommend Monday
  00:00 server time). Enforced **backend-side** on `/api/spend` (it owns the
  ledger); `/buy` reports "cap reached, resets in Nd Nh".
- **Progression gate** via `requires_boss` — e.g. Ashlands food needs
  `defeated_fader`, so you can't buy end-game food as a Meadows newbie.
- **Earnable only** — everything sold is something a player can already
  make/farm; nothing exclusive. Sold in small x5 bundles, not stacks of 50.
- **Not sold** (would skip core mining/exploration): raw ore & bars (Iron,
  Silver, Black metal, Flametal), Surtling cores, Refined Eitr, Chitin, Ancient
  seeds, Dragon tears.

Proposed catalog (prices anchored to `donor_badge` = 500):

| SKU | Gate | Bundle | Price | Weekly cap |
|---|---|---|---|---|
| `food_t1` — Sausages, Blood Pudding, Serpent Stew | `defeated_bonemass` | x5 ea | 120 | 4 |
| `food_t2` — Lox Meat Pie, Bread, Fish Wraps | `defeated_goblinking` | x5 ea | 180 | 3 |
| `food_t3` — Misthare Supreme, Mushroom Omelette, Yggdrasil Porridge | `defeated_queen` | x5 ea | 260 | 2 |
| `food_t4` — top Ashlands dishes | `defeated_fader` | x5 ea | 350 | 2 |
| `meads_utility` — Tasty, Frost Res, Poison Res | — | x5 ea | 100 | 3 |
| `meads_vitality` — Medium Healing, Medium Stamina | `defeated_bonemass` | x5 ea | 160 | 2 |
| `meads_eitr` — Minor Eitr | `defeated_queen` | x5 | 160 | 2 |
| `farm_bundle` — Barley, Flax, Onion/Carrot/Turnip seeds | `defeated_goblinking` | x20 ea | 120 | 2 |
| `forage_bundle` — Coal, Resin, Feathers, Thistle, Dandelion, Honey | — | x50/x20 | 100 | 2 |

The `grant_item` effect + weekly cap + `requires_boss` gate are **now
implemented** (`Catalog.cs`, `ShopHandler.cs`, `/api/spend`) — see
[PLUGIN.md](PLUGIN.md) and [BACKEND.md](BACKEND.md). To go live you only need to
add the SKUs to `valcoin_shop.yaml` (copy from the example) and confirm the
Ashlands food prefab ids for your Valheim version. Adding a plain `grant_perk`
SKU is still just a YAML edit.

## In-game UI panel (F8)

If the plugin is installed **client-side too**, press **F8** (configurable)
to open a minimal IMGUI panel:

```
┌─── Valheim Donations ──────── [X] ┐
│ Balance: 1500 c                   │
│ Perks: donor_badge, companion_flair│
│ [Donate] [Shop] [Gift] [Top]      │
│ ───────────────────────────────── │
│ < per-tab content >               │
│ ───────────────────────────────── │
│ Messages (server replies)         │
└───────────────────────────────────┘
```

- **Donate tab** — one button. Calls the backend, displays your code + URL.
- **Shop tab** — scrollable SKU list with "Buy" buttons; shows owned/charges and
  (for `grant_item` SKUs) the weekly cap remaining per row.
- **Gift tab** — recipient + amount fields, "Send gift" button. Also exposes the
  `/title` editor when you own that perk.
- **Top tab** — leaderboard of lifetime donors.

The panel auto-closes when you open inventory, map, or pause menu. Sends
commands via a silent `vc_action` RPC so nothing appears in public chat.

Vanilla clients (no plugin) keep working — all the same actions are available
via the chat commands above. The panel is pure quality-of-life for modded
clients. (The F4 Donation Codex above is the fuller, browsable version.)

## Advertising the donation system

Defaults-light advertising kit — the goal is to make the donation flow
discoverable without nagging players.

| Approach | Status | Annoyance | Notes |
|---|---|---|---|
| **F4 Donation Codex** | 🔜 | none | The browsable, opt-in home for economy, perks, and Top Patrons. |
| **One-time HUD on join** | Built, **default ON** | very low | Single TopLeft line, 5s after spawn. Toggle via `welcome_message_enabled`; customise via `welcome_message` in `valcoin_config.json`. |
| **Donor ⭐ badge in chat** | Built | none | Pure passive social proof — donors show off by chatting. |
| **`/topdonors` leaderboard** | Built | none | Opt-in: players type the command or open the Top tab / F4 Patrons board. |
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

- [valheim-plugin/Flows.cs](../valheim-plugin/Flows.cs) — command implementations
- [valheim-plugin/ShopHandler.cs](../valheim-plugin/ShopHandler.cs) — `ApplyEffect` dispatch (add `grant_item` here)
- [valheim-plugin/Catalog.cs](../valheim-plugin/Catalog.cs) — YAML loader (add `item` / `weekly_cap` / `requires_boss`)
- [valheim-plugin/DonationPanel.cs](../valheim-plugin/DonationPanel.cs) — F8 panel
- [valheim-plugin/examples/valcoin_shop.example.yaml](../valheim-plugin/examples/valcoin_shop.example.yaml) — proposed catalog
