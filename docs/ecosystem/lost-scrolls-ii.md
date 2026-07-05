# Lost Scrolls II — Knowledge Notes

**Repo folder:** `E:\Valheim Modding\Dvergr Expanded` (renamed to "Lost Scrolls II"; GUID `com.lostscrollsii`)
**Type:** Server-side gameplay mod (optional client UI), vanilla assets only.
Spiritual sequel to the deprecated "Lost Scrolls" (TaegukGaming).

## Concept

Free a corrupted **Dvergr** instead of killing it — the **Communion Rite**
turns it into a levelable ally that fights beside you, tends workstations,
sails ships, and follows through portals.

## Core features

- **Recruitment** — subdue then free a Dvergr (four castes: Rogue, Fire Mage,
  Ice Mage, Support Mage). Persisted on the creature ZDO (`DE_Recruited`).
- **Leveling** — levels **1–10**, biome-/HP-scaled kill XP + player-kill XP,
  custom gold `★N` badge. Per-caste bonuses.
- **Commands** — feed/heal (mead, `G`), stance cycle Follow/Guard/Standby (`E`),
  rename (`Y`), duel toggle (`J`), chore recall (`H`). Owner-gated except feeding.
- **Chores** — caste-gated station automation: Fire→smelting, Ice→refining,
  Support→provisioning/farm/husbandry, Rogue→haul. Persists across relog.
- **Dvergr duels** — non-lethal, **multiplayer-only** companion sparring for
  bonus XP (needs two players; can't solo-farm).
- **Communion Totems** — seal an ally into a carriable totem (Incinerator +
  Wisps ritual) and summon it back, level intact.
- **Travel** — Follow-stance allies board ships & teleport through portals.
- **Minimap pins** — client-side pins for your *own* companions only.
- **Betrayal** — striking a companion with a butcher knife turns it feral.

## Story delivery

Lore is delivered **through ValheimServerGuide** (soft dep), as a biome-by-biome
descent (Meadows → Ashlands) using `distance` triggers, plus a Companion
Handbook. Ships as two Thunderstore packages: base gameplay mod + a "Quest"
complete pack.

Author-only roadmap (kept out of all in-game text to avoid spoilers): a future
**finale boss + "Armor of God" gear set**.

## Donation-relevant hooks

- **Companion cosmetics** are the sweet spot: names, owner tags, and the floating
  badge are already customizable → cosmetic-only donor flair fits perfectly.
- Recruiting / leveling / duel wins already fire `dvergr_recruited`,
  `dvergr_level_up`, `dvergr_duel_won` events into ServerGuide → these are ready
  made triggers to *celebrate* milestones (and optionally mention donations).
- **Do NOT** sell XP, extra companion slots, or combat buffs — that breaks the
  earn-through-play balance. See [donation-hooks.md](donation-hooks.md).
