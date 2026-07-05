# Donation Promotion Ideas — Ecosystem Hooks

Ways the four mods can surface the donation system **organically**, without
being salesy or pay-to-win. Guiding rules first, then per-mod ideas.

## Design rules (the balance guardrails)

1. **Never sell permanent power or progression skips.** No stats, no XP, no gear,
   no extra companion slots, no faster Lord respawns, no skipping a boss/biome
   gate. If it *permanently* changes who wins a fight or lets you leapfrog the
   progression curve, it's off the table.
   - **Consumables are the one allowed exception, under strict limits.** Food,
     meads, and grind-heavy *earnable* materials can be sold because they're
     temporary and self-depleting — but only with (a) a **weekly per-player
     cap**, (b) they're things a player *can already make/farm* (convenience,
     not exclusivity), and (c) **progression gating** so you can't buy Ashlands
     food as a Meadows newbie. See "Weekly-limited consumables" below.
2. **Sell identity, expression, convenience, and gratitude** — cosmetics,
   titles, name flair, decoration, "thank you" moments, and *quality-of-life
   that saves real-world tedium but not in-game challenge*.
3. **Promote at moments of joy, never friction.** Celebrate a boss/Lord kill or a
   companion milestone; never interrupt a fight or nag on a timer that annoys.
4. **Reuse ServerGuide** for almost all messaging — it's the vanilla-UI engine
   that already reaches every player (guaranteed by ServerGuard). New plugin
   code should be the last resort.
5. **Everything opt-in or ambient.** Codex entries, NPC dialogue, passive badges
   — players choose to look. One low-key join line is the max "push."

---

## Cross-cutting (ServerGuide + ServerGuard as the delivery layer)

- **Exclusive Donation Codex (F4).** A dedicated donations-only Codex panel,
  separate from ServerGuide's F3 guide Codex, opened with **F4** (configurable).
  It is the single home for the whole donation surface:
  - **How it works** — Valcoins, `/donate`, the claim-code flow.
  - **Economy & commands** — every supporting command in one place: `/coins`,
    `/shop`, `/buy <sku>`, `/gift <player> <amount>`, `/topdonors`, `/title`,
    plus the F8 quick panel.
  - **Perks** — the current cosmetic + consumable catalog with prices and any
    weekly limits remaining.
  - **Top Patrons** — a leaderboard section inside the Codex mirroring
    `/topdonors` (lifetime top donors), refreshed on open. This is the
    passive-social-proof surface that lives *in* the Codex, not in chat.

  Implementation note: this is the one promotion surface that is **not** pure
  ServerGuide YAML — a donations-only Codex + Patrons board is new plugin UI
  (can reuse ServerGuide's Codex layout patterns / vanilla IMGUI like the F8
  panel). Everything *inside* it reads existing backend data (`/shop`,
  `/topdonors`, balance).
- **Haldor "Support" conversation.** A `conversation`-mode entry on Haldor:
  hold-E gives an in-lore option — *"Rumor says patrons of this realm are
  remembered…"* → explains donations, links the portal. Fits the trader fantasy,
  no chat spam. *(Pure YAML.)*
- **Polite periodic reminder.** A `timed` entry, interval ≥ 60 min, `raven` mode,
  one sentence, `stop_when` the player has already donated. This replaces the
  "periodic chat broadcast" the donations wishlist rated medium-annoyance with a
  much gentler vanilla popup. *(Pure YAML.)*
- **Unified Discord feed.** Point the donations backend webhook, ServerGuide's
  webhook, and ServerGuard's webhook at the **same channel** → donations appear
  alongside boss kills and joins as community highlights, not ads.
- **100% reach is guaranteed.** Because ServerGuard requires the modpack, the
  donation plugin can be a `required_mod` — the F8 panel, `/donate`, welcome line,
  and every ServerGuide promo reach *every* connected player.

---

## BiomeLords hooks

- **"Sponsored by" Lord-kill beat.** On `boss_defeated` / Lord death, a `global`
  ServerGuide message: *"The <Lord> falls! This realm endures thanks to its
  patrons — ⭐ <top donor>."* Celebratory, once per kill, ties the server's
  biggest fights to gratitude, not sales. *(YAML + reuse `/topdonors` data.)*
- **Lord-hunt milestone titles.** "Lordslayer" chat title unlocked by *playing*
  (all 7 Lords), not buying — but a donor-exclusive *color/style* of that title
  is a cosmetic reward. Achievement stays earned; flair is the perk.

## Lost Scrolls II (Dvergr companions) hooks

- **Companion cosmetic flair (best fit).** Companions already support custom
  names, owner tags, and a floating `★N` badge. Donor perk = cosmetic-only:
  a **special badge color / name style** for a donor's companions, or a
  "Patron" prefix on the owner tag. No stat change — a Fire Mage is still a
  Fire Mage. *(New cosmetic perk; leans on existing name/badge rendering.)*
- **Milestone celebration, not sale.** `dvergr_recruited` / `dvergr_level_up`
  (level 10) / `dvergr_duel_won` already fire into ServerGuide. Author warm
  `raven`/`rune` congratulations for these. On the *first-ever* recruit, a
  single gentle footnote can mention the Support Codex — after that, never again.
  *(Pure YAML.)*
- **Explicitly OFF-limits:** selling XP, levels, extra companions, revive-from-
  totem shortcuts, or duel wins. The whole mod is earn-through-play; protect it.

## Valheim-ServerGuard hooks

- **Guarantor, not a gate.** ServerGuard's value to donations is that it makes
  every promo reach every player. **Never** gate joining, whitelisting, or
  modpack access behind a donation — that's pay-to-play and violates its
  fairness purpose.
- **Optional donor cosmetic on the join log / Discord.** ServerGuard already
  posts connection events to Discord; a donor could get a small ⭐ next to their
  name in that feed (cosmetic recognition only). *(Minor, optional.)*

---

## Weekly-limited consumables & materials (new shop tier)

Sell **hard-to-cook food, meads, and grind-heavy items** — capped **per player
per week** so donations top up a supply, never replace playing the game.

**Mechanics (implemented — `Catalog.cs`, `ShopHandler.cs`, `/api/spend`):**
- Shop effect `grant_item` spawns the item(s) as ItemDrops at the buyer's feet
  (server-authoritative; works for vanilla clients), the backend debits Valcoins
  atomically via `/spend`.
- **Weekly cap** tracked per player per SKU, reset on a fixed weekly boundary
  (recommend Monday 00:00 server time). Backend owns the counter (it owns the
  ledger); `/buy` returns "cap reached, resets in Nd Nh".
- **Progression gate** (recommended): a SKU lists a `requires_boss`; the plugin
  refuses the purchase until that boss is down for the player/world. Keeps
  Ashlands food out of Meadows hands.
- Sold in **small bundles** (x5 portions), not stacks of 50 — a boost, not a pantry.

**Proposed catalog** (prices anchored to existing scale: donor_badge = 500):

| SKU | Tier / gate | Bundle | Price | Weekly cap | Why it's "hard" |
|---|---|---|---|---|---|
| **T1 food** (Sausages, Blood Pudding, Serpent Stew) | Swamp — Bonemass | x5 | 120 | 4 | Multi-step recipes / serpent hunting |
| **T2 food** (Lox Meat Pie, Bread, Fish Wraps) | Plains — Yagluth | x5 | 180 | 3 | Barley/flax farming gate |
| **T3 food** (Misthare Supreme, Mushroom Omelette, Sea Bass) | Mistlands — Queen | x5 | 260 | 2 | Mistlands ingredient grind |
| **T4 food** (top Ashlands dishes) | Ashlands — Fader | x5 | 350 | 2 | End-game, most tedious cooks |
| **Utility meads** (Tasty, Frost/Poison/Fire res, Stamina) | by biome | x5 | 100 | 3 | Honey + coal + foraging grind |
| **Healing / Eitr meads** (Medium Healing, Minor/Med Eitr) | Swamp / Mistlands | x5 | 160 | 2 | Eitr refinery gate |
| **Farm bundle** (Barley, Flax, Onion/Carrot/Turnip seeds) | Plains for barley/flax | x20 | 120 | 2 | Slow crop cycles |
| **Forage bundle** (Coal, Resin, Feathers, Thistle, Dandelion, Honey) | none | x50 | 100 | 2 | Pure tedium, no gate |

**Deliberately NOT sold** (too progression-sensitive — recommend excluding, or
if ever added, gate hard + tiny cap): raw ore/bars (Iron, Silver, Black metal,
Flametal), Surtling cores, Refined Eitr, Chitin, Ancient seeds, Dragon tears.
Selling these skips the mining/exploration that *is* the game.

**Open decision for you:** should progression gating be **on** (recommended —
you must have killed the tier's boss to buy its food) or **off** (buy anything,
weekly cap is the only limit)? Gating protects the difficulty curve; off is
simpler and more generous.

---

## Recommended first slice (highest value, lowest risk)

1. **Exclusive Donation Codex (F4) + Top Patrons board** — the single home for
   economy, commands, perks, and the patron leaderboard. *(New plugin UI.)*
2. **Haldor "Support" conversation** — the flagship in-lore, opt-in ask. *(YAML —
   drafted in [../../valheim-plugin/examples/guidance.donations.yaml](../../valheim-plugin/examples/guidance.donations.yaml).)*
3. **Lord-kill "sponsored by top donor" global beat** — celebratory, ties the
   marquee moments to gratitude. Drafted as generic boss-kill gratitude beats in
   the same file (dynamic donor name needs a plugin hook). *(YAML + top-donor data.)*
4. **Companion + Lordslayer cosmetic donor flair** — pure-cosmetic perk code,
   on-brand with vanilla-asset ethos.
5. **Weekly-limited consumables shop** — food / meads / forage bundles with
   per-week caps (+ optional boss gating). *(New `grant_item` effect + weekly
   counter.)*
6. **Unified Discord webhook channel** — one community feed across all three
   webhook-capable mods.

Items 2, 3, 6 are configuration/YAML with no balance risk. Items 1, 4, 5 are the
code changes: 1 and 4 stay cosmetic; 5 is the one gameplay-touching tier, kept
fair by weekly caps + earnable-only + progression gating.
