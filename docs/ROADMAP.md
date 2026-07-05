# Roadmap — Planned Perks

Ideas discussed and scoped but **not yet implemented**. Kept here so design
decisions (validation gates, pricing, portability rules) survive between
sessions instead of living only in chat history.

## Shared rule: crafter-only gate

All four perks below require the target item's `m_crafterID` to match the
acting player's Steam64 (`player.GetPlayerID()`). This blocks trade/service
exploits (buying a perk once, then "servicing" other players' gear) and
keeps the cosmetic ones meaningful (`{CrafterName}'s ...` only makes sense
if the player actually crafted it). When implemented, this should be a
shared helper (e.g. `PerkManager.AssertCrafterOwnsItem`) rather than
duplicated per perk.

## 1. Personalized Armor Name

Vanilla-safe (tooltip renders from synced `ItemData` fields — no client mod
required to see it).

- Format: `{CrafterName}'s {BaseItem} of the {AuraName}`
- Suffix is derived from the chosen glow/aura in perk #2 — not freely
  chosen, and rewrites automatically if the aura changes
- Stored per-instance via `m_customData`, surfaced through a Harmony patch
  on `ItemDrop.ItemData.GetTooltip`
- Crafter-only (see above)

## 2. Visual Customization Bundle

Requires the plugin client-side to render — vanilla clients see the base
armor with no visual change (but do see the name from perk #1, since that's
tooltip text, not a model change).

- Recolor / tint via `MaterialPropertyBlock` swap on the armor's renderer
- Emissive / glow (pump the emission channel)
- Particle aura on equip (VFX prefab parented to a player bone while worn)
- Drives the suffix in perk #1
- Broadcast via the existing `vc_action` RPC so all modded viewers re-skin
  the wearer
- Crafter-only

## 3. Overcharge — Stat Over-Upgrade

- **Gate:** item must be at `m_quality == m_shared.m_maxQuality` first
- Bumps `m_quality` past the cap → armor/damage/durability scale one tier
  higher automatically (vanilla stat formulas, no extra patch needed for
  the numbers themselves)
- Cap at +1 initially (~2000c); a second, pricier perk could later unlock +2
- Crafter-only
- Tooltip line when implemented: `<color=orange>Overcharged +N</color>`

### Server-gating (Option B — chosen)

Overcharge should be restricted to "this server" without needing a
save/logout hook (Option C was considered and rejected as too fragile —
character-file strip/restore around disconnects risks losing state on
crash-disconnects).

- On apply: stamp `m_customData["vc_overcharge_origin"] = SERVER_GUID`
- On player join: sweep inventory; any item whose `vc_overcharge_origin`
  doesn't match this server's GUID gets `m_quality` re-clamped down to
  `m_maxQuality`
- **Server GUID**: generate once on first boot, persist to
  `BepInEx/config/valcoin_data/server_guid.json`, never rotate (rotating
  would orphan every existing overcharge on the server that minted it)
- Net effect: active on the minting server ✅, re-clamped on other
  plugin'd servers ✅, still active on fully vanilla servers (accepted
  tradeoff — no patch runs there to undo anything)
- The join-time sweep belongs in `PerkManager` (or a new `OverchargeGuard`)
  hooked into the existing player-join codepath — not a per-frame check

## 4. Reinforce — Durability Over-Upgrade

- Same "must be at max tier" gate as Overcharge
- Boosts durability **only**, via a Harmony postfix on
  `ItemDrop.ItemData.GetMaxDurability` reading `m_customData["vc_reinforced"]`
- Tier-bump model: each stack adds one `m_durabilityPerLevel` worth, capped
  at +3 total
- Stacks cleanly with Overcharge — different math terms (durability tier
  bump vs. quality bump), so they compound rather than conflict
- Cheaper than Overcharge (~800c) — pure QoL, no combat ceiling lift, so no
  extra server-gating needed
- Crafter-only
- **Naturally server-bound already**: the effect only exists because of the
  Harmony patch. `m_customData` travels with the item to other worlds, but
  is dormant there without the plugin running — no Option B/C logic needed
- Tooltip line when implemented: `<color=#7ec0ee>Reinforced +N</color>`

## Suggested build order

1. **Personalized Armor Name** — foundation; lowest risk, vanilla-safe
2. **Reinforce** — validates the "must be max tier" gating pattern with the
   lowest gameplay-balance risk
3. **Overcharge** — bigger gameplay impact; reuses the gating pattern from
   Reinforce, adds the Option B server-guard logic
4. **Visual Customization Bundle** — highest engineering cost (client-side
   rendering, asset considerations), and depends on #1's suffix wiring

## Source of truth

This file is the only record of these decisions until implementation
starts — once code lands, move the relevant sections into
[SHOP.md](SHOP.md) and [PLUGIN.md](PLUGIN.md) and trim this file back to
whatever's still unbuilt.
