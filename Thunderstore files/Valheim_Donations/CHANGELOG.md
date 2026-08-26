# Changelog

## 5.21.1

- **The shop no longer refuses coins you actually have — for real this time.** The shop was checking your balance against a file on the server instead of the donation ledger that holds your coins. That file only learned a balance by adding up what it happened to see, starting from zero for anyone it had never recorded — so a player with thousands of Valcoins could finish one 2-coin daily quest and be told they had exactly 2, and refused a purchase on the spot. Your balance is now read from one place only: the same ledger the panel shows you. Same fix for gifting and for staked events.
- **Coins given by an admin are now real coins.** The Admin tab wrote them to that server-side file and nowhere else, so they showed up in no panel and could not be spent — they looked like coins right up until you tried to use them. Giving and removing now go through the ledger, and a gift from an admin arrives with the usual "+N Valcoins!" the same way a donation does.
- **The balance in the "+N Valcoins!" popup is now the true one.** It is read back from the ledger after each payout rather than being a running total the server kept for itself, so it can no longer drift away from what your panel says.

*No configuration changes. Nothing to reinstall beyond the update itself.*

## 5.21.0

- **There is now a Donations button on your inventory screen.** F4 still opens the panel and always will, but a hotkey nobody told you about is not something you can discover — and that panel is the only way into donating, the shop, gifting and the patron board. The button sits with the others at the top of your inventory, styled like the game's own, so the feature is findable without anyone having to tell you the key. On servers running Lost Scrolls II it sits directly under **Rankings**; on servers without it, alone at the top centre.

**For server operators:**

- On by default. Set `"inventory_button_enabled": false` in `valcoin_config.json` to hide it (existing config files keep the button — the key defaults to on when absent).
- Client-side only, and no new dependency: the button is a clone of the inventory's own "Take All" button, and its position is read from the Lost Scrolls II row at runtime by name. That mod being absent, disabled or updated cannot break it.

## 5.20.0

Mostly a groundwork release — but one change you will notice if your server runs event prizes.

- **Event prizes are no longer trimmed by the daily cap.** Valcoins earned from daily quests are still capped at 8 a day, which is what that cap is for. But a prize you *won* — a tournament purse, a bounty reward — was being squeezed through the same allowance, so a 100-coin prize paid 8 at best, and nothing at all if you had already done your dailies that morning. Those payouts are now paid in full and no longer eat into your daily quest allowance.
- **Other mods can now charge and pay Valcoins.** This is what makes staked events possible: a companion mod can take an entry fee, refund it if the event is cancelled, and pay out a purse. It stays server-side and it sells nothing new — no perk, item or advantage became purchasable. First used by Lost Scrolls II's wagered tournaments and duel invites.

**For server operators:**

- Needs backend **0.10.0** — mark a quest `capped: false` in `valcoin_quests.yaml` to exempt it from the daily allowance. Without the newer backend the flag is ignored and prizes are capped as before.
- The shipped `valcoin_quests.yaml` template now includes the Lost Scrolls II event prizes (`ls_tournament_prize`, `ls_bounty_t1`…`t5`) as uncapped examples.
- Exempt does **not** mean unlimited: each quest id still pays at most once per UTC day, which is what bounds those payouts now that the coin cap doesn't.

## 5.19.3

- **Familiars now fly at your left shoulder instead of your right.** Same height, same distance — just the other side, where they sit clear of your shield arm and your camera. Applies to all nine familiars.
- **The Fallen Valkyrie has lost her smoke.** At familiar size her full-scale smoke trail read as a grey smudge following your head around. It's gone, replaced with the Wraith's spectral glow. Her glowing eye trails are untouched.

*Cosmetic only — no stats, prices or shop items changed, and nothing to reconfigure. Backend unaffected.*

## 5.19.2

- **The shop no longer refuses coins you actually have.** It checked your balance against the server's local cache first, and a player that cache had never seen was treated as having zero — so you'd be told *"Not enough Valcoins (0 / 1300)"* while your coins sat safely on the backend. It now only refuses when it genuinely knows you're short, and otherwise lets the backend decide. Same fix for gifting.
- **Grants can no longer go missing after a server restart.** If the server failed to write its balance file, it still told the backend the coins had been delivered — so they were never re-sent, and the balance reverted next restart. It now keeps the grant pending and retries instead.

## 5.19.1

- **"Get my donation code" no longer hangs.** If the server didn't answer, the panel sat on *"Requesting your code..."* forever — no error, no way to retry short of restarting the game, so a server-side hiccup looked like a dead button. It now gives up after 20 seconds, tells you what happened, and lets you press again immediately.

## 5.19.0

*Everything in 5.18.0 below ships with this release too — 5.18.0 was never
published, so upgrading from 5.17.0 gets you both.*

- **The donation steps now tell you the truth.** The F4 Donate tab used to say your code would already be filled in for you. On Ko-fi it never was — Ko-fi can't prefill its message box — so donations arrived with no code attached and credited nothing until an admin sorted it out by hand. The panel now says plainly: on Ko-fi, paste your code into the message box yourself.
- **It also tells you what the other providers need.** GCash/Maya carries your code automatically, and Patreon needs a one-time account link. The panel never mentioned that Patreon step before, even though first-time patrons can't be credited without it.
- **Forgot to paste your code?** The donation portal now has an "Already donated?" box on the Ko-fi card — enter the email you donated with and your Valcoins are credited on the spot, no admin needed.
- **PayPal removed.** Reliable auto-crediting needs a PayPal business account, which this server doesn't have; without one, PayPal donations could never credit themselves. Ko-fi, Patreon and GCash/Maya are unaffected.

> **Server operators:** the Ko-fi rescue form needs backend **0.7.1 or later**.
> Everything else here is panel text and works against any backend. The 5.18.0
> quest system below still needs **0.7.0+** and ValheimServerGuide.

## 5.18.0

- **Earn Valcoins by playing.** A one-time welcome quest worth **30 Valcoins** introduces the donation system, and a pool of **daily quests** gives a small amount each day — so there's a reason to log in, and a way to try the shop without spending anything.
- **The welcome quest: "The Patron's Welcome."** Use a crafting station, cook at a fire, then find Haldor in the Black Forest and ask him about the patrons. He explains what Valcoins are and hands over the welcome share. If a trader camp has already been discovered nearby, a map pin points the way.
- **Dailies.** Be online a while (2), cull 15 common creatures (3), tame any creature (2), fell a Biome Lord (8), or recruit/level/duel a Dvergr companion (5). **You can earn at most 8 per day** — the pool adds up to more than that on purpose, so you do whatever suits the session rather than grinding a checklist. Resets at 00:00 UTC.
- **Streak bonus.** Claim a quest on 7 days in a row for **+20 Valcoins**, and again every 7 days after.
- **Check your standing any time.** The F4 panel shows *"Daily quests: 5/8 · resets in 6h 12m · 4-day streak"* under your balance, on every tab.
- Quest earnings **do not** appear on the Top Patrons board — that stays a record of actual donations.

> **Server operators:** this needs a backend running **0.7.0 or later**; against
> an older one every completed quest silently pays nothing. It also needs
> **ValheimServerGuide**, which provides the quests themselves — copy
> `guidance.valcoin-quests.yaml` from the repo into your server's
> `BepInEx/config/ValheimServerGuide/`. ServerGuide itself needs no
> modification. Payout values live in `BepInEx/config/valcoin_quests.yaml`,
> auto-templated on first run; the daily cap, streak length and streak bonus
> are backend settings.
>
> Without ServerGuide installed, nothing here activates and the rest of the mod
> is unaffected.

## 5.17.0

- **Familiars now survive armor upgrades.** Upgrading the helmet your familiar is bound to no longer makes it disappear. The familiar (and the helmet's name suffix) carries over to the upgraded piece, and the helmet goes straight back on, so your companion is there without you having to re-equip anything.
- **The crafting panel tells you before you upgrade.** Selecting a helmet that carries a familiar now shows its familiar name — e.g. *Bronze Helmet of the Bat* — along with the familiar's attack bonus and a note that it's kept through the upgrade. No more wondering whether you're about to lose it.

> Nothing to do on the server side: this release is client-side only and needs no
> backend update. Familiars bought before 5.17.0 are unaffected and will now
> survive upgrades like any other.

## 5.16.0

- **Shop preview images.** Shop items can now show a picture. A new optional `preview_image` field on each SKU in `valcoin_shop.yaml` takes either an `https` URL or a path relative to `BepInEx/config` (e.g. `shop_images/Bat.png`). The shipped Familiars catalog uses it, so you can finally see what you're buying.
- **Click to enlarge.** Preview images appear as a thumbnail in the Shop row and larger in the purchase-confirm dialog — clicking either opens a full-size view, fitted to your window and never blown up past the image's real size. Close it with the Close button, a click outside, or Escape.
- **Exchange rate on the Donate tab.** The Donate tab now leads with a large `$1 USD = 50 Valcoins` callout plus a worked example, so you know what a donation is worth before you open your wallet. The Shop tab carries the same rate as a one-line note. The rate comes from the server, so it can never drift from what donations actually credit.

> Server operators: the exchange-rate callout needs a backend running 5.16.0 or
> later (it serves the new `coins_per_usd` field). Older backends make the
> callout read "Exchange rate unavailable". Preview images set as
> config-relative paths only resolve on machines that hold the image files —
> use `https` URLs so every connecting client can load them.

## 5.15.0

- **Familiars now grant small perks.** While a familiar helmet is equipped you get **feather fall** (the Feather Cape's own effect — wearing both doesn't stack) and a **tiny flat attack bonus** matched to the creature: Bat/Ghost/Wraith +2 slash, Deathsquito +2 pierce, Drake Hatchling +2 frost, Volture +3 pierce, Gjall +2 blunt & +1 fire, Fallen Valkyrie +2 spirit. The bonus is a fraction of a percent of endgame weapon damage — flavor, not power — and applies to melee, bows, and magic alike.
- **Overwrite warning.** If your equipped helmet already carries a familiar, the purchase-confirm modal now warns that buying a new one overwrites it, before you spend.
- **Soulkeeper Charm: 10 charges per week.** The charge pool is now capped at 10 per player per week, shared across the x1/x5/x10 tiers. Over-cap purchases are rejected with no coins spent and a "resets in …" note.
- **Tomb creature repel.** When the Valkyrie sets you down at your tombstone, hostile creatures nearby are staggered and shoved away so you aren't instantly mobbed. (Not a lasting safe zone — they can wander back.)
- **Gjall familiar** no longer drips tar, and its (and the Fallen Valkyrie's) particle effects are scaled down to match the mini-pet size.

## 5.14.0

- **Familiar fixes.** The Drake Hatchling now animates instead of freezing; a harmless "Can't remove Humanoid" log spam on some familiars is gone; Volture, Gjall, and Fallen Valkyrie hover a bit higher and read more clearly.
- **Spawn/despawn effect.** A small puff of effect plays when a familiar appears (helmet equipped) or disappears (unequipped).

## 5.13.0

- **Familiars.** The armor-effect category is now eight **miniature flying creatures** — Bat, Ghost, Deathsquito, Drake Hatchling, Wraith, Volture, Gjall, and Fallen Valkyrie — that hover at your shoulder, bound to your equipped helmet (which gains a matching name suffix). Other players running the mod see them too. Priced by progression tier (400–1300 Valcoins).

## 5.10.0

- **Armor effects.** New `armor_vfx` shop effect attaches a cosmetic aura to a chosen equipped armor piece, broadcast to other players via ZDO. (Reworked into Familiars in 5.13.)

## 5.9.0

- **Native panel skin + purchase confirmation.** The donation panel is restyled to match Valheim's own UI, and clicking **Buy** now opens a **Yes / Cancel** confirmation before any Valcoins are spent.

## 5.8.0

- **Grouped shop.** The Shop tab now groups items into categories (Soulkeeper Charms, Familiars, Feasts, Meads, Supplies) with one description per category, instead of one long flat list.

## 5.7.0

- **Soulkeeper Charm.** A new death-insurance consumable: on death you keep your skills (no skill drain) and a Valkyrie carries you from the spawn point back to your tombstone. Bought as stackable charges; backend-tracked.
- **Removed cosmetic chat perks.** The donor badge and chat title were removed — on a dedicated server their chat rendering was unreliable. Replaced by the Soulkeeper Charm and other consumables.

## 5.3.0

- **One combined panel, one hotkey.** The separate F4 "Codex" and F8 "quick panel" are merged into a single donation panel — open it with **F4** (the F8 hotkey was removed). Tabs: Donate, Shop, Gift, Patrons, and Admin (admins only).
- **Reworked Donate tab.** Clicking "Get my donation code" now shows the code **inline, right below the button**, with a **Copy code** button and an **Open donation portal** button that launches your default web browser (no more copy-pasting a raw link). Clear step-by-step instructions live on the same tab.
- **Anti-spam cooldown** on generating a donation code (30s), with a live countdown.
- **Terms of Use** link on the Donate tab opens an in-game modal with the donation terms.
- **Fixed the "square" icons.** Valheim's in-game font can't render emoji, so every emoji in the UI and server messages was replaced with clean text — no more blank squares.
- **More readable buttons** — the primary donate action uses a high-contrast gold style.

## 5.2.0

- **Removed all chat and console commands** (`/donate`, `/coins`, `/shop`, `/buy`, `/gift`, `/topdonors`, `/title`, `/givecoins`, `/removecoins`). The chat-command hook proved unreliable on servers running other chat-patching mods; the F4 Codex and F8 panel already covered every action over a reliable silent RPC.
- **⚠️ Breaking: the plugin is now required client-side to use the donation system at all.** Vanilla (un-modded) clients can no longer donate/shop/gift — only players running the plugin can open the F4/F8 panels. A passive donor-badge/chat-title chat prefix still works for everyone.
- **New Admin tab (F8 panel)**: give/remove a player's Valcoin balance from the UI, replacing the removed `/givecoins`/`/removecoins` commands. Only visible to Steam64 IDs listed in `valcoin_admins.yaml`.

## 5.1.0

- **F8 panel now tracks live backend reachability**, same as the F4 Codex: it polls periodically while open, flips an online/offline state on real fetch success/failure (not just "config has a URL"), and dims the Buy/Donate/Gift buttons while offline.
- **Shop catalog now syncs to remote clients over RPC.** Previously `valcoin_shop.yaml` only existed on whichever machine loaded it, so vanilla/remote clients connecting to a dedicated server saw an empty shop. The server now broadcasts its parsed catalog to every connected client every 30 seconds.

## 5.0.0

Initial public release.

- **Valcoin economy**: `/donate`, `/coins`, `/shop`, `/buy`, `/gift`, `/topdonors`, `/title`, plus admin `/givecoins` and `/removecoins`.
- **Four donation providers**: Ko-fi, PayPal, Patreon, PayMongo — each independently optional, verified via its own webhook signature.
- **F4 Donation Codex**: browsable, offline-resilient home for the economy, shop catalog, and patron leaderboard. Lights up automatically when the backend comes online — no client update needed.
- **F8 quick panel**: Donate / Shop / Gift / Top tabs for players who also run the plugin.
- **Mouse-cursor fix** for both panels — click and navigate with the mouse instead of hotkeys only.
- **`grant_item` shop effect**: weekly-capped, boss-gated consumable items (hard-to-cook food, meads, rare materials) spawned directly into the buyer's inventory — works for vanilla clients too.
- **Vanilla-client compatible**: every command works via chat even without the plugin installed client-side.
