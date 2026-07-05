# Mod Ecosystem — Knowledge Base

Reference notes on the other Valheim mods by the same author, kept here so the
donations system can be designed to *complement* them without breaking game
balance or feeling pay-to-win.

All four mods share two design DNA traits worth remembering:

- **Vanilla assets only** — no custom models/textures; everything reuses
  existing Valheim prefabs and UI. Donation perks should respect this aesthetic.
- **Server-authoritative** — the server owns state; clients just display.

| Mod | One-liner | Repo |
|---|---|---|
| [BiomeLords](biomelords.md) | Per-biome "Lord" super-creatures granting Blessings + Forsaken Powers | ValheimBiomeLords |
| [Lost Scrolls II](lost-scrolls-ii.md) | Recruitable, levelable Dvergr companions (formerly "Dvergr Expanded") | Lost-Scrolls-II |
| [ValheimServerGuide](server-guide.md) | YAML-driven in-game guidance / quests / NPC dialogue | ValheimServerGuide |
| [Valheim-ServerGuard](server-guard.md) | Locks the server to an approved modpack; kicks vanilla/mismatched clients | Valheim-ServerGuard |

See [donation-hooks.md](donation-hooks.md) for how each mod can surface the
donation system organically.
