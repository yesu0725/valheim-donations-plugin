# BiomeLords — Knowledge Notes

**Repo folder:** `E:\Valheim Modding\ValheimBiomeLords`
**Type:** Client+server gameplay mod, vanilla assets only.

## Concept

Every biome hides a **Lord** — a towering, named version of one of that biome's
creatures (Neck, Greydwarf Shaman, Draugr Elite, Fenring, Lox, Seeker, Fallen
Valkyrie). Seven Lords total, Meadows → Ashlands.

## Core loop

1. Craft a **Lord's Horn**.
2. Prove yourself by hunting the biome's regular creatures.
3. Return to the biome **at night**, use the Horn to summon the Lord.
4. Defeat it → automatically gain its **Forsaken Power** + its trophy.
5. Mount the trophy on a **Lord's Pedestal** → draw its **Blessing**.

## Blessings (permanent passive; one active at a time)

Mutually exclusive — drawing a new one replaces the current. Each mounted
trophy has **limited charges (5 default)** then crumbles; short cooldown
between draws; trophy is locked to the pedestal until spent.

- **Fisher's Boon** (Neck) — chance to save bait + bonus fish.
- **Quick Sprout** (Greydwarf Shaman) — crops within 30m grow faster.
- **Iron Vein** (Draugr Elite) — chance for extra iron from swamp ore.
- **Pack Whisperer** (Fenring) — nearby tamed wolves take less damage; faster taming/breeding.
- **Hearth Master** (Lox) — food buffs last 2×.
- **Refiner's Touch** (Seeker) — smelters/furnaces/wheels/refineries chance for bonus output.
- **Featherweight** (Fallen Valkyrie) — huge carry weight w/o penalty + 2 extra inventory rows.

## Forsaken Powers (active, vanilla-power rules: ~10 min / 20 min cooldown)

Tide's Grace, Forest's Embrace, Plague Bearer, Howl of the Pack, Bull Rush,
Hive Sight, Valkyrie's Rally (fires once, full CD; full party heal + shield).

## Difficulty scaling (important for balance)

- Lord **health** scales up with the *furthest* vanilla boss you've killed
  (never below its home-biome baseline) — early Lords stay meaningful late-game.
- Lord **damage** is capped at +1 tier above its home biome — never one-shots.
- Admins can override per-Lord toughness on a server.

## Donation-relevant hooks

- Trophy **charges crumble** → natural, non-P2W place to offer a cosmetic or a
  convenience (NOT a stat boost). See [donation-hooks.md](donation-hooks.md).
- Defeating a Lord is a big, celebratory moment → good "sponsored by" / Discord
  brag beat.
- Purely cosmetic **Hall of the Lords** decoration ideas fit the vanilla-asset
  ethos without touching power.
