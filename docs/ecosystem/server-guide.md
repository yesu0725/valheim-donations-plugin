# ValheimServerGuide — Knowledge Notes

**Repo folder:** `E:\Valheim Modding\Valheim ServerGuide`
GUID `com.valheimserverguide`. Server-authoritative, YAML-driven, vanilla UI only.
Built on BepInEx 5 + HarmonyX + Jötunn.

**This is the most important mod for the donation system** — it's the generic
in-game "say something to the player when X happens" engine. Almost every
donation-promotion idea can be authored as `guidance.yaml` entries with **zero
new code**.

## How it works

Server loads `BepInEx/config/ValheimServerGuide/guidance.yaml`, hot-reloads on
change, and syncs it to all clients (Jötunn RPC). Clients run Harmony patches +
a dispatcher that matches game events against the synced config.

## Trigger types (fires when…)

`craft`, `item_acquired`, `kill`, `build`, `distance` (within radius of a point),
`biome`, `skill_level`, `discover_location`, `damage_type`, `npc_interacted`,
`npc_conversation` (hold E on trader), `npc_item_submit` (use item on trader),
`boss_defeated`, `first_login`, `player_death`, `chest_opened`, `timed`
(repeating interval), `entry_finished`, plus Lost Scrolls II events
(`dvergr_recruited`, `dvergr_duel_won`, `dvergr_level_up`).

## Display modes (all vanilla UI)

`raven` (Hugin popup), `message` (toast), `chat`, `rune` (lore tablet),
`intro` (cinematic + music), `conversation` (NPC dialogue panel, hold-E on
Haldor/Hildir/BogWitch, choice buttons).

## Firing semantics

`once`, `cooldown`, `requires`, `stop_when`, `scope: player|global`, `version`.

## Rewards (granted on completion / conversation choice)

`item`, `skill_exp`, `skill_level`, `buff`. A center-screen notification
summarises what was granted. (17 reward types exist in the codebase per CLAUDE.md.)

## Other systems

- **Guide chains** — multi-step quests, progress counters, HUD tracker (F10) +
  Codex (F3).
- **NPC conversations** — dialogue trees with reward-granting choices.
- **Discord integration** — server-side webhook POSTs when entries fire / chains
  complete. Templates: `{playerName} {id} {topic} {text}`.
- **Admin commands** — `vsg_reset`, `vsg_list`, etc.

## Donation-relevant hooks (huge)

- The **`conversation` mode on Haldor** = a ready-made, lore-friendly "Support
  the server" NPC dialogue with no chat spam.
- **`timed` + `cooldown`** = the polite periodic reminder the donations SHOP.md
  wishlist wanted — already exists, just author a YAML entry.
- **Discord webhook** on entry-fire = the "brag beat" amplifier for boss kills,
  Lord kills, milestone donations.
- **Codex category "Support"** = a permanent, non-nagging place donation info can
  live (players open it themselves).
- Anything I want to "say to the player" for donations should almost always be a
  guidance.yaml entry, not new plugin code.
