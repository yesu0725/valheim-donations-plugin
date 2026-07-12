# Plugin

BepInEx server-side plugin that polls the donations backend and applies
granted Valcoins to players. **All donation actions go through the in-game
panel (open with F4)** — there are no chat-typed or console commands (removed; see
[SHOP.md](SHOP.md#no-chat-or-console-commands)). The plugin must be installed
client-side to use the donation system at all.

## Layout

- [Plugin.cs](../valheim-plugin/Plugin.cs) — BepInEx entry, admin YAML, Harmony patch (current version **5.7.0**)
- [GrantPoller.cs](../valheim-plugin/GrantPoller.cs) — polls `/api/grants/pending`
- [CatalogSync.cs](../valheim-plugin/CatalogSync.cs) — broadcasts the shop catalog to remote clients over RPC
- [CoinManager.cs](../valheim-plugin/CoinManager.cs) — balance cache + applied-grant dedupe
- [ShopHandler.cs](../valheim-plugin/ShopHandler.cs) — purchase validation, effect dispatch (`grant_item` / `add_charges`)
- [PerkManager.cs](../valheim-plugin/PerkManager.cs) — per-player perks (legacy helpers; charge pools are now backend-authoritative)
- [Catalog.cs](../valheim-plugin/Catalog.cs) — loads `valcoin_shop.yaml`; serializes for `CatalogSync`
- [Flows.cs](../valheim-plugin/Flows.cs) — donate / gift / leaderboard implementations, shared by the router
- [Soulkeeper.cs](../valheim-plugin/Soulkeeper.cs) — Soulkeeper Charm Phase 1: caches the local
  charge count (`SoulkeeperPoller`), and on death consumes a charge to skip the skill drain
  (`Player.OnDeath` + `Skills.LowerAllSkills` patches). Backend-authoritative via `/api/charges/consume`
- [ValkyrieCarry.cs](../valheim-plugin/ValkyrieCarry.cs) — Soulkeeper Charm Phase 2: after a warded
  death, on respawn the intro Valkyrie carries the player from the spawn point to their tombstone
  (fade transition, ESC-menu + auto-pickup suppressed during flight, distance-scaled watchdog with a
  plain-teleport fallback)
- [LocalIdentity.cs](../valheim-plugin/LocalIdentity.cs) — `Steam64()` resolver extracted so the
  pollers can resolve the local id without the panel
- [DonationPanel.cs](../valheim-plugin/DonationPanel.cs) — the single combined client-side
  IMGUI panel (opens with F4); tabs: Donate / Shop / Gift / Patrons / Admin. Offline-resilient;
  Donate tab has inline code + Copy + Open-portal + cooldown + Terms modal. Renders owned/weekly/charge
  states from `/api/state`; header shows a persistent "Charges:" line for held consumables
- [DonationUiState.cs](../valheim-plugin/DonationUiState.cs) — blocks all game input (ZInput reads +
  Minimap/Inventory hard-guards) while the panel is open, so typing an amount can't open the map/inventory
- [RpcLayer.cs](../valheim-plugin/RpcLayer.cs) + [UiActionRouter.cs](../valheim-plugin/UiActionRouter.cs)
  — `vc_action` silent RPC for panel → server actions (the only input path)
- [BackendClient.cs](../valheim-plugin/BackendClient.cs) — UnityWebRequest wrapper
- [SteamIdResolver.cs](../valheim-plugin/SteamIdResolver.cs) — Steam64 + PlayFab support
- [Utils.cs](../valheim-plugin/Utils.cs) — `SharedCoroutineRunner`, shared by the static handler classes

> **Removed:** `ChatDecoration.cs` (donor-badge/chat-title chat prefix) was deleted along with the
> cosmetic `donor_badge` / `chat_title` / `companion_flair` / `lordslayer_title` SKUs — on this
> dedicated-server build the peer-to-peer chat routing and missing `NetworkUserId` made the effect
> unreliable. Replaced by the Soulkeeper Charm consumable. See [STATUS.md](STATUS.md).

For the user-facing panel layout and shop schema, see [SHOP.md](SHOP.md).

## Ecosystem shop extensions

**Built** — the `grant_item` pipeline:

- **`Catalog.cs`** — `Sku` carries `Item` / `WeeklyCap` / `RequiresBoss` /
  `Charges`; parser handles `item` / `weekly_cap` / `requires_boss` / `charges`;
  `Commit` requires `item` for `grant_item` SKUs (and `perk` for `grant_perk` /
  `add_charges`) instead of dropping every SKU with no `perk`.
- **`ShopHandler.cs`** — `ApplyEffect` has a `grant_item` case that spawns
  ItemDrops at the buyer's feet (server-authoritative; works for vanilla
  clients, matching ServerGuide's reward-drop pattern). Pre-checks the
  `requires_boss` gate (`ZoneSystem.GetGlobalKey`) and character presence
  *before* debiting, guards against re-applying a `duplicate` spend, and
  surfaces the backend's 429 "weekly limit" message. `Buy()` forwards
  `grant_charges` / `charge_kind` for `add_charges` SKUs.
- **`backend/app/routes/spend.py`** — `/api/spend` accepts `weekly_cap` and
  enforces a per-player, per-SKU count within the current week (Monday 00:00
  UTC), returning **429** with a "resets in Nd Nh" detail; also credits
  `add_charges` pools. See [BACKEND.md](BACKEND.md).

**Built** — the `add_charges` pipeline (Soulkeeper Charm): a backend-authoritative
consumable charge pool. See the file list above (`Soulkeeper.cs`,
`ValkyrieCarry.cs`) and [SHOP.md](SHOP.md#add_charges-effect--soulkeeper-charm-death-insurance-).

**Dropped** (were cosmetic `grant_perk` perks): `companion_flair` /
`lordslayer_title` / `donor_badge` / `chat_title` — the client-side rendering
proved unreliable on this dedicated-server build (peer-to-peer chat, no
`NetworkUserId`), so they were removed in favor of the Soulkeeper Charm. The
still-unbuilt armor perks live in [ROADMAP.md](ROADMAP.md).

Operator config + the ServerGuide promo YAML:
[../valheim-plugin/examples/valcoin_shop.example.yaml](../valheim-plugin/examples/valcoin_shop.example.yaml),
[../valheim-plugin/examples/guidance.donations.yaml](../valheim-plugin/examples/guidance.donations.yaml).

## Required DLLs in `libs/`

The csproj expects these in `valheim-plugin/libs/`. Most come from Valheim's
`valheim_Data/Managed/` folder:

- `0Harmony.dll` (BepInEx)
- `BepInEx.dll`
- `assembly_valheim.dll`
- `UnityEngine.dll`
- `UnityEngine.CoreModule.dll`
- **`UnityEngine.UnityWebRequestModule.dll`** ← needed for HTTPS polling. Copy
  from `valheim_Data/Managed/`.
- **`UnityEngine.IMGUIModule.dll`** ← needed for the in-game panel.
  Same location.
- **`UnityEngine.InputLegacyModule.dll`** ← needed for the F4 keybind. Same.
- **`UnityEngine.TextRenderingModule.dll`** ← needed for `FontStyle` in the panel styles. Same.
- `Newtonsoft.Json.dll`

`YamlDotNet` and `Jotunn` are **no longer required** — admin YAML uses a
built-in regex parser, and there's no chat-command parsing anymore either. See
[STATUS.md](STATUS.md) for the build-artifact discrepancy this created.

## Build

```powershell
cd valheim-plugin
dotnet build -c Release
# → bin\Release\net472\ValheimDonationSystem.dll → drop into BepInEx\plugins\
```

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

Admins are listed in `BepInEx/config/valcoin_admins.yaml` (Steam64 IDs only) —
auto-generated with a placeholder on first run. Restart the server after
editing.

## Persistent state files

| Path | Purpose |
|---|---|
| `BepInEx/config/valcoin_config.json` | backend URL + token + poll interval |
| `BepInEx/config/valcoin_admins.yaml` | admin Steam64 list |
| `BepInEx/config/valcoin_shop.yaml` | SKU catalog |
| `BepInEx/config/valcoin_data/coin_balances.json` | balances + applied-grant cache |
| `BepInEx/config/valcoin_data/perks.json` | per-player perks/charges/title/home |

The backend's SQLite is the source of truth — the plugin's local balance
cache only answers panel/Codex balance queries instantly without a network
round-trip.

## Vanilla-client compatibility

**The plugin must be installed client-side to use the donation system at
all.** All actions (donate/buy/gift/admin) route through the in-game panel's
(F4) silent RPC — there is no chat or console command path (removed; see
[SHOP.md](SHOP.md#no-chat-or-console-commands) for why). A truly vanilla
client can connect but won't be able to donate, shop, or gift.

On a ServerGuard-locked server this is moot — every connecting player already
runs the modpack, including this plugin — but it's a real requirement change
for anyone deploying this mod standalone without ServerGuard.

`grant_item` consumables still spawn items server-side at the buyer's feet
(not written into a remote inventory directly), so *delivery* doesn't depend
on client mods — only the purchase trigger does.

## Source of truth

- [valheim-plugin/Plugin.cs](../valheim-plugin/Plugin.cs) — version, startup sequence
- [valheim-plugin/ValheimDonationSystem.csproj](../valheim-plugin/ValheimDonationSystem.csproj) — required DLLs
- [valheim-plugin/README.md](../valheim-plugin/README.md) — original detailed reference
