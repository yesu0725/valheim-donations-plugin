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
│ Perks: donor_badge, companion_flair    │
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
- **Gift tab** — recipient + amount fields, "Send gift" button. Also exposes the
  chat-title editor when you own the `chat_title` perk.
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
| `chat_title` | grant_perk | Unlocks the chat-title editor (Gift tab) to set a `[Bracket]` prefix | ✅ |
| `companion_flair` | grant_perk | Donor-only badge colour / name style on your Lost Scrolls II Dvergr (cosmetic only) | 🔜 |
| `lordslayer_title` | grant_perk | Gilded colour of the *earned* Lordslayer title (must have slain all 7 BiomeLords) | 🔜 |

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
