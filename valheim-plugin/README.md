# Valheim Donations — BepInEx Plugin

Server-side plugin that polls the donations backend for granted Valcoins and
applies them to players. **All donation actions go through the F4
panel — there is no chat-typed or console command anymore** (removed; see
"No chat commands" below). The plugin must be installed **client-side** to
use the donation system at all, not just for quality-of-life.

## No chat or console commands

Chat-typed slash commands (`/donate`, `/buy`, `/gift`, etc.) and their admin
equivalents (`/givecoins`, `/removecoins`) were removed — the reflection-based
`Chat.RPC_ChatMessage` hook they relied on proved unreliable on a server
running several other mods that also patch chat, and the UI panels already
covered the same actions over a silent RPC. See [docs/SHOP.md](../docs/SHOP.md)
for the full writeup and consequences (vanilla-client support is gone; the
plugin is now required client-side).

There are no chat-decoration perks. Donor badge / chat title were removed: on a
dedicated server chat is routed peer-to-peer, so reliable per-player cosmetic
decoration would require a server→client perk registry plus cross-mod chat/hud
patch ordering — fragile enough that the perks were dropped in favour of
consumable + convenience rewards.

Admins are listed in `BepInEx/config/valcoin_admins.yaml` (Steam64 IDs only) —
used by the panel's Admin tab (give/remove a player's balance).

## Shop catalog

SKUs live in `BepInEx/config/valcoin_shop.yaml` (auto-generated on first run).
Each SKU has:

```yaml
shop:
  soulkeeper_10:                  # add_charges — backend-tracked consumable pool
    name: "Soulkeeper Charm (x10)"
    description: "On death, keep your skills - no skill drain. Adds 10 charges."
    price: 1300
    effect: add_charges           # grant_perk | add_charges | grant_item
    perk: soulkeeper              # charge kind (the pool key)
    charges: 10                   # charges credited per purchase

  food_t2:                        # grant_item — weekly-limited consumable
    name: "Plains Feast (bundle)"
    price: 180
    effect: grant_item            # spawns item stacks at the buyer's feet
    item: "LoxPie:5,Bread:5,FishWraps:5"   # comma list of prefab or prefab:qty
    weekly_cap: 3                 # max purchases per player per week (0 = unlimited)
    requires_boss: defeated_goblinking     # global boss key gate (optional)
```

**Effects** (the `effect:` field):

| Effect        | What it does                                                              | Status |
|---------------|--------------------------------------------------------------------------|--------|
| `grant_item`  | Spawns weekly-capped, optionally boss-gated item stacks at the buyer's feet | built  |
| `add_charges` | Credits a backend-tracked consumable charge pool (the shipped **Soulkeeper Charm** — on death skip the skill drain + Valkyrie tombstone carry) | built  |
| `grant_perk`  | Flips a passive `PerkManager` flag — generic mechanism, but no SKU ships one today | supported |

> **Removed cosmetics:** `donor_badge` / `chat_title` / `companion_flair` /
> `lordslayer_title` (and `ChatDecoration.cs`) were dropped — client-side chat
> rendering was unreliable on this dedicated-server build (peer-to-peer chat, no
> `NetworkUserId`). Replaced by the Soulkeeper Charm. Still-unbuilt armor perks
> are in [../docs/ROADMAP.md](../docs/ROADMAP.md).

The live catalog is **12 SKUs** (3 Soulkeeper Charm tiers + 9 `grant_item`
bundles). Add `grant_item` / `add_charges` SKUs by editing the YAML; new effect
types require a code change in `ShopHandler.cs::ApplyEffect`. Full catalog +
rationale: [examples/valcoin_shop.example.yaml](examples/valcoin_shop.example.yaml),
[../docs/SHOP.md](../docs/SHOP.md), and
[../docs/ecosystem/donation-hooks.md](../docs/ecosystem/donation-hooks.md).

## Donation Codex (F4)

A dedicated donations-only Codex panel (separate from ServerGuide's F3 Codex),
opened with **F4**. Houses: how-it-works, the perk catalog with weekly limits
remaining, and a **Top Patrons** leaderboard section. Fully offline-resilient
— browsable before the backend is even configured, lights up live once it's
online. Buy/donate actions themselves live in the same panel.

## Persistent state files

- `BepInEx/config/valcoin_config.json`           — backend URL + token
- `BepInEx/config/valcoin_admins.yaml`           — admin Steam64 list
- `BepInEx/config/valcoin_shop.yaml`             — SKU catalog
- `BepInEx/config/valcoin_data/coin_balances.json` — balances + applied-grant cache
- `BepInEx/config/valcoin_data/perks.json`       — per-player perks (legacy; charge pools are now backend-authoritative in the `charges` table)
- `BepInEx/config/valcoin_shop.example.yaml`     — see `examples/` for the proposed ecosystem catalog

## In-game UI panel (F4)

The plugin must be installed **client-side too** for this panel to exist —
press **F4** (configurable) to open it:

```
┌─── Valheim Donations ─────────── [X] ┐
│ Balance: 1500 Valcoins               │
│ Charges: Soulkeeper Charm x3         │  <- only shows when you hold charges
│ [Donate] [Shop] [Gift] [Patrons] [Admin]│  <- Admin only shows for admins
│ ──────────────────────────────────── │
│ < per-tab content >                  │
│ ──────────────────────────────────── │
│ Messages (server replies)            │
└──────────────────────────────────────┘
```

- **Donate tab** — one button. Calls the backend, displays your code + URL.
- **Shop tab** — scrollable SKU list with "Buy" buttons. Renders authoritative
  state from `/api/state`: one-time buys show "Already Purchased" (Buy disabled),
  boss-gated items show "Unlocks after <boss>", weekly items show "N of M left
  this week" (Buy disables at the cap, re-enables after the Monday reset), and
  `add_charges` items show how many charges you hold.
- **Gift tab** — recipient + amount fields, "Send gift" button.
- **Patrons tab** — leaderboard of lifetime donors.
- **Admin tab** — give/remove a player's Valcoin balance manually. Only
  rendered once the server confirms (via a `whoami` RPC) the local Steam64 is
  in `valcoin_admins.yaml`.

The panel auto-closes when you open inventory, map, or pause menu. Sends
every action via a silent `vc_action` RPC so nothing appears in public chat.

## Advertising the donation system

This system ships with a defaults-light advertising kit. The goal is to make
the donation flow discoverable without nagging players.

| Approach | Status | Annoyance | Notes |
|---|---|---|---|
| **F4 Donation Codex + Top Patrons** | built | none | Browsable, opt-in home for economy, perks, and the patron leaderboard. |
| **One-time HUD on join** | Built, **default ON** | very low | Single TopLeft line, 5s after spawn, points at F4. Toggle via `welcome_message_enabled`; customise text via `welcome_message` in `valcoin_config.json`. |
| **Donor ⭐ badge in chat** | Built | none | Pure passive social proof — donors show off by chatting. |
| **Top Patrons leaderboard** | Built | none | Opt-in: open the Patrons tab (F4). |
| **Haldor "Support" conversation** | proposed (ServerGuide YAML) | none | In-lore hold-E dialogue — no new code, pure `guidance.yaml`. |
| **Lord-kill "sponsored by top donor" beat** | proposed (ServerGuide YAML) | low | Celebratory global message on a BiomeLord/boss kill. |
| **Gentle `timed` reminder** | proposed (ServerGuide YAML) | low | Raven popup ≥ 60 min, `stop_when` already-donated. Replaces the old "periodic chat broadcast" idea. |
| **Unified Discord webhook** | proposed | none in-game | Point donations + ServerGuide + ServerGuard webhooks at one channel. |
| **Spawn-area signs** | manual | none | Admin places signs saying "Press F4 for donations" or links a URL. Zero plugin code. |

Because **Valheim-ServerGuard** locks the server to the modpack, the ServerGuide
YAML promos above reach 100% of players. See
[../docs/ecosystem/donation-hooks.md](../docs/ecosystem/donation-hooks.md) for
the full ecosystem plan and recommended first slice.

## Vanilla-client compatibility

**The plugin must be installed client-side to use the donation system at
all.** Earlier versions supported truly vanilla (un-modded) clients via a
chat-command hook; that was removed for reliability (see
[docs/SHOP.md](../docs/SHOP.md)), so `/donate`/buy/gift/etc. now only exist as
F4 panel actions, which require the plugin's client-side UI. On a
ServerGuard-locked server this doesn't matter — every connecting player
already runs the plugin as part of the modpack — but it's a real requirement
if you deploy this mod standalone.

The `grant_item` consumables still spawn items directly into the player's
inventory server-side (not written client-side), so delivery itself works
regardless of client mods — the purchase trigger is what now requires the
client plugin.

## Required DLLs in `libs/`

The csproj expects these in `libs/`. Most come from Valheim's
`valheim_Data/Managed/` folder:

- `0Harmony.dll` (BepInEx)
- `BepInEx.dll`
- `assembly_valheim.dll`
- `UnityEngine.dll`
- `UnityEngine.CoreModule.dll`
- **`UnityEngine.UnityWebRequestModule.dll`** ← needed for HTTPS polling. Copy
  from `valheim_Data/Managed/`.
- **`UnityEngine.IMGUIModule.dll`** ← needed for the in-game panel (Phase 5).
  Same location.
- **`UnityEngine.InputLegacyModule.dll`** ← needed for the F4 keybind. Same.
- `Newtonsoft.Json.dll`

(YamlDotNet and Jotunn are no longer required — admin YAML uses a built-in
regex parser, and slash commands are server-side only.)

## Building from source

> **Heads-up for fresh clones:** `libs/` is **gitignored** and is **not** in
> this repo. It holds Valheim's and Unity's assemblies (`assembly_valheim.dll`,
> `UnityEngine.*.dll`), which are copyrighted and cannot be redistributed. You
> must supply them yourself from your own game install before the build will
> compile.

1. **Install the .NET SDK** (targets `net472`; the .NET SDK 6.0+ can build it).

2. **Populate `libs/`** with the DLLs listed under
   [Required DLLs in `libs/`](#required-dlls-in-libs). Copy them out of your own
   install — most live in `valheim_Data/Managed/` (client) or the dedicated
   server's `valheim_server_Data/Managed/`, and `0Harmony.dll` / `BepInEx.dll`
   come from your BepInEx `core/` folder. Match them to **one** install so the
   assembly versions are consistent.

3. **Build:**

   ```bash
   dotnet build -c Release
   ```

   Output: `bin/Release/ValheimDonationSystem.dll` → drop into
   `BepInEx/plugins/` on the server (and the client too, if you want the F4
   panels).

4. **Deploy to both halves** (optional, Windows): `deploy.ps1` builds Release and
   copies the DLL to the client and dedicated-server plugin folders in one step —
   edit the two paths at the top for your machine. Stop the dedicated server
   first, or the copy fails because the running process holds a lock on the DLL.

## Configuration

On first launch the plugin writes a template
`BepInEx/config/valcoin_config.json`:

```json
{
  "backend_url":  "https://your-app.fly.dev",
  "plugin_token": "paste-the-PLUGIN_TOKEN-from-your-fly-secrets",
  "poll_interval_seconds": 10
}
```

`plugin_token` must match the `PLUGIN_TOKEN` secret on the backend. Env vars
`VALCOIN_BACKEND_URL` and `VALCOIN_PLUGIN_TOKEN` override the file (handy if
your host's panel exposes env vars but not config files).

## Data files

- `BepInEx/config/valcoin_config.json`  — backend URL + token
- `BepInEx/config/valcoin_admins.yaml`  — admin Steam64 list
- `BepInEx/config/valcoin_data/coin_balances.json` — running balances cache

The authoritative ledger lives in the backend's SQLite. The plugin's local
cache is only used to answer `/coins` instantly without a network round-trip.
