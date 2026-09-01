# Status Snapshot

This file decays fastest of anything in `docs/` — check the actual source
files before trusting it if it's been a while. For *what changed* rather than
*what's true now*, see [CHANGELOG.md](CHANGELOG.md), which also carries the
plugin↔backend compatibility matrix.

## Where things stand — 2026-09-01

**Deployed / published right now.** These drift apart constantly; check them
before believing any bug report (see the DLL-timestamp trap below).

| Target | Plugin | Notes |
|---|---|---|
| **Thunderstore (public)** | **5.22.2** | Published 2026-08-31 13:33 UTC. **This is what every player's client runs**, and it carries the relog bug 5.22.3 fixes -- a client-side fault, so this is the only number that matters until 5.22.3 is published. |
| **Dedicated server** | **5.23.0** | Promoted 2026-09-01 while stopped, twice (5.22.4 then 5.23.0). DLL SHA-256 matches `bin/Release`; `manifest.json` bumped in place each time, keeping its own dependency list (no `denikson-BepInExPack_Valheim` -- that is the client pack). **Not started since**, so nothing has loaded it yet. 5.23.0 is cosmetic and client-only, so the server gains nothing from it beyond version parity. |
| **Every Gale profile (test client)** | **5.23.0** | Deployed 2026-09-01. All four verified by SHA-256, not by assumption: `HB Test`, `Hearthbound Valheim`, `HB Modpack Ref` took it through the hard link; **`Hearthbound - Admin` has its own file** and was written separately by the script's verification pass, on both deploys -- see the corrected hard-link note below. |
| `bin/Release` | **5.23.0** | Clean build (0 errors), 2026-09-01. SHA-256 `FCC1701626D7...`. |
| Thunderstore **zip** | **5.23.0** | `Valheim_Donations-v5.23.0_20260901-1424.zip`, built 2026-09-01. Five files flat at the root; DLL hash-checked against `bin/Release` before zipping (the staged copy is always a release behind until step 4 is done -- it was stale for 5.22.3 too). **Ready to upload; upload is the owner's step.** The older `v5.22.3` zip is superseded -- 5.23.0 contains it whole -- and kept only as history. |

**Two versions in flight, on purpose.** **5.22.3** is tested, packaged and
waiting on nothing but the Thunderstore upload -- it fixes the relog bug (RPC
handlers registered once per process instead of once per session, so from the
second login onward a client was debited and never delivered).
**5.22.4** adds the listen-server host fix, is deployed locally, and is
**untested in a hosted world**.

**5.23.0 is TESTED, COMMITTED AND PACKAGED. The one step left is the
Thunderstore upload**, which is the owner's to make: there is no `tcli` on this
machine and no Thunderstore token in the environment, and publishing is a public
release under their account either way.

Every local target is on 5.23.0 -- all four Gale profiles and the dedicated
server -- and the owner has confirmed in-game that the familiar layout file and
the local-world purchase fix both work.

**5.22.4 is a burned number:** it was deployed everywhere and superseded by
5.23.0 the same day, without ever being published. Its host-of-a-local-world fix
is contained whole in 5.23.0. Same rule that burned 5.18.0, 5.22.0 and 5.22.1.

Recommended publish order is still: **upload the 5.22.3 zip.** It is the one
players need, it is verified in-game, and holding it behind an untested
low-priority change helps nobody. 5.22.4 can follow once a hosted world has
actually been tried. The 5.22.3 zip on disk is unaffected by the version bump in
the staging folder.

For a client talking to a dedicated server the two are behaviourally identical:
5.22.4 contains 5.22.3 whole, and every branch it changes is host-only. That is
also why promoting the server was safe.

**Verified in-game by the owner, 2026-09-01**, over the sequence that used to
break it -- purchase on the dedicated server, out to a locally hosted seed
world, back to the dedicated server, purchase again. Both server purchases
succeeded. Three sessions in one process with a role change in the middle, which
exercises more than the original bug report did: the middle session made the
client a listen server, so the plugin had to re-read its role rather than latch
the first session's.

That upload is the step that actually fixes players, because the fault is
entirely client-side. Deploying to Gale fixed only these machines; the server
promotion was for parity and fixes nothing on its own -- a dedicated server's
`ZNet` lives for the whole process, so its registration never went stale.

**The first 5.22.3 test failed because nothing had been deployed.** The fix was
built and reported as done while `bin/Release` was the only place it existed;
every Gale profile and the dedicated server were still running the 5.22.2 DLL
from 2026-08-31. Hashing all six copies took a minute and settled it before any
code was re-examined. Do that first, always -- it is now the first line of the
DLL-timestamp trap note below.

5.22.0 and 5.22.1 are **burned numbers** — each ran on HB Test, each had faults
found there, neither was published; same rule that burned 5.18.0. Their zips are
still on disk next to the published one; ignore them.

**Gale hard-links mod files across profiles -- but NOT all of them.** The
2026-08-31 note here said all four profiles share one NTFS inode and drew the
conclusion that `deploy.ps1` has never been "test profile only". The first half
was wrong. Measured again 2026-09-01: `HB Test`, `Hearthbound Valheim` and
`HB Modpack Ref` share inode `...1547b6`; **`Hearthbound - Admin` has its own
file** and does not follow a deploy. Gale re-links on its own schedule, so this
is an observation with a date on it, never a rule.

The conclusion still holds -- a deploy does reach the played profile, so it was
never test-only -- but the mechanism is no longer trusted to do it.
`deploy.ps1` now hashes every Gale profile after copying and writes into any
that did not match, printing `ok`/`UPDATED` per profile; that is what caught the
Admin profile. The dedicated server's copy is standalone and unaffected, and
promoting to it stays manual. Details in [OPERATIONS.md](OPERATIONS.md).

**Still unexercised: the purchase retry/refund path.** Everything else in
5.22.2 has now been used in anger, but the retry and the compensating refund
only run when the backend is slow, unreachable, or answers a spend it then
cannot deliver — none of which happens on a healthy day. It is the one part of
this release whose first real execution will be in front of a player. Forcing it
once (stop the backend mid-buy; point a SKU at a bogus prefab id) would settle
it cheaply. Both leave a trail: the refund logs
`[Valcoin] refunded N to <id> for undelivered <sku>` and shows up in the ledger
as an `admin` grant noted `refund: <sku> could not be delivered`.

**Open items, highest value first.**

1. **270 Valcoins owed to three players, not yet settled.** `vc_welcome` is
   `period: once` and its payout was raised **30 → 300** after these three had
   already claimed it, so they can never claim the difference:
   `76561199062837584` (Taeguk), `76561198064737621` (Xyn),
   `76561198075620490` (Traveler Z). Settle by hand from the Admin tab if you
   want them made whole — that tab only reaches the real ledger on **5.21.1+**
   (before that it edited the local cache and the coins were unspendable).
   Owner's call; deliberately not done.
2. **Gap in the 5.21.1 reconcile, not yet closed.** `GrantPoller.Reconcile`
   only runs for players who have a grant in the *current* pending batch — `Tick`
   returns early when the queue is empty — so a player who never earns another
   grant keeps a stale cached balance indefinitely. Nothing gates on that cache
   any more, so the blast radius is the "+N Valcoins!" toast figure and
   `ValcoinWallet.BalanceOf` (advisory, used by sibling mods for display). A
   periodic reconcile over connected players would close it.
3. **Nothing is published.** Until the 5.23.0 zip is uploaded, every player is
   still on **5.22.2** and stops being able to buy anything after their first
   relog of a session. That is the one genuinely urgent item here, and it has
   been true since 2026-09-01. Upload at
   <https://thunderstore.io/c/valheim/create/docs/>.

**FIXED in 5.22.4: the host of a locally hosted world can use the mod.** Found
2026-09-01 while testing the relog fix -- a purchase on the dedicated server
worked, the same purchase on a local seed world did nothing, silently. It was
recorded here as a known limitation, then fixed the same day.

One confusion in four places: code meaning *"I am a headless server with no
player here"* asked `IsServer()`, which is also true for a host.
`UiActionRouter` looked the sender up in ZNet's peer list (remote connections
only -- a host is not in it) and returned; `RpcLayer.HandlePanelOnClient` threw
away replies that had been delivered correctly; `SteamIdResolver.ZdoFor` /
`OnlinePlayerFor` could not find the buyer to deliver to; and `ArmorVfxManager`
/ `SoulkeeperPoller` skipped their work entirely. All now key on
`senderPeerID == ZNet.GetUID()` or `IsDedicated()` as appropriate. Full account
in [CHANGELOG.md](CHANGELOG.md).

**Dedicated-server behaviour is unchanged** -- every altered branch is the one a
dedicated server was already taking. **Not yet tested in a hosted world**; the
mechanism was verified by decompiling the game's `ZRoutedRpc`/`ZNet` and against
the owner's BepInEx log from the listen-server session, but the fix itself has
not been exercised. Nobody plays as a host here, so it was not made a blocker.

**Trap: `valheim-plugin/libs/assembly_valheim.dll` is the DEDICATED SERVER's
assembly, not the client's.** Byte-identical to the server install's copy
(SHA-256 `84A1B34F...`, 2,119,680 bytes); the client's is a different file
(`3B26C851...`, 2,126,848 bytes). Decompile the one in `libs/` and
`ZNet.IsDedicated()` reads `return true;`, because in that build it is a
compile-time constant. It is a **reference** assembly -- calls bind by name at
runtime, so a client runs the client's real implementation. The 5.22.3
registration logic depends on `IsDedicated()` being false on a client and
demonstrably works (the owner's log shows both the server and the client RPC
registration on their listen server, which requires exactly that). **Do not
"correct" working `IsDedicated()` logic from that decompilation** -- and if you
need to read client behaviour, decompile
`Steam\steamapps\common\Valheim\valheim_Data\Managed\assembly_valheim.dll`
instead.


**Closed this round, recorded so it isn't re-investigated.**

- **`76561198128047618` (HearthboundJero) "did not receive" 300 — they did.**
  Backend balance **315**, and their ledger rows sum to exactly 315.
  `coin_balances.json` said **15**: its `recentGrants` holds 52 and 54 but skips
  **53**, which is the 300. The cache is not the ledger. Why that one grant
  failed to record locally is unrecoverable — `LogOutput.log` is overwritten on
  every restart — and no longer matters, since 5.21.1 stopped gating on the file
  and it self-corrects on that player's next grant. **Do not reset the quest to
  "retry" it:** `period: once` pays `already_claimed` forever, the pending queue
  is empty (nothing in limbo), and a re-pay would double-credit them.
- **The daily cap was never the problem.** The backend applies it to
  `period: daily` quests only — verified against the ledger, where 300
  (`vc_welcome`) passed untouched on a day the same player's dailies were trimmed
  to exactly 8. One-time quests, including every `gospel_*`, are exempt by
  construction and need no `capped: false`.
- **`vc_welcome_haldor` is a ServerGuide entry, not a Valcoin quest id.** It is
  step 2 of 2 and its reward sets `VC.Q.vc_welcome`, so the payout id is
  `vc_welcome`. Nothing to fix.

Full reasoning for all of the above is in the 5.21.1 and 5.21.2 entries of
[CHANGELOG.md](CHANGELOG.md); how to query the live ledger is in
[OPERATIONS.md](OPERATIONS.md).

- **Project phase:** 6+ — backend is **live on Fly.io** with **three providers
  configured** (Ko-fi, Patreon, PayMongo; PayPal removed 2026-08-11 — needs a
  business account this server doesn't have), plugin catalog
  syncs to remote clients, chat/console commands removed in favor of UI-only.
  Shop now ships a **Soulkeeper Charm** consumable (backend charge ledger +
  in-game skill-save + Valkyrie tombstone carry); cosmetic badge/title/flair
  perks were dropped.
- **Backend version:** `0.10.0` — **deployed to Fly.io 2026-08-23** and verified
  live: `/openapi.json` reports `0.10.0` and `ClaimRequest` carries
  `capped` (`boolean`, `default: true`). Adds the opt-out from the daily quest
  cap for event prizes. Committed as `05fb198` in the backend repo before the
  deploy, so the running image traces to a real commit. `init_db()` ran clean
  against the existing volume, and the dedicated server's GrantPoller was
  already polling `/api/grants/pending` with 200s within seconds of the restart.
  Webhook regression probes after the deploy: patreon 401, paymongo 401 (both
  configured & verifying), paypal 503 (removed 2026-08-11, expected), kofi 422 —
  Ko-fi takes `data: str = Form(...)`, so an empty probe fails form validation
  before the signature check; 422 is correct for that route and the "401 means
  configured" rule in DEPLOYMENT.md does not apply to it.
  Pushed to `origin/main` 2026-08-23.
- **Older note, kept for the deployment history:** `0.7.0` — **deployed to Fly.io 2026-08-08** and verified
  live: `/openapi.json` reports `0.7.0`, `POST /api/quests/claim` and
  `POST /api/admin/quest-reset` are present, and `/api/state` carries the four
  `quest_*` fields. Adds ServerGuide quest rewards (`quest_claims` table, daily
  cap, streak bonus). No migration ran — `quest_claims` is
  `CREATE TABLE IF NOT EXISTS` and no existing table was touched. Committed as
  `fba6db9`, so the deployed image matches a real commit (unlike the 2026-07-13
  deploy — see [CHANGELOG.md](CHANGELOG.md)). Tests: 84 passing.
  **Deployed and reachable at `https://valheim-donations.fly.dev`** — redeployed
  2026-07-19 adding **`coins_per_usd` to `/api/state`** (from
  `settings.coins_per_unit["USD"]`, currently **50**) so the in-game panel can
  show an authoritative exchange rate instead of hard-coding one. Verified live:
  `/api/state/<id>` returns `coins_per_usd: 50.0`. The 2026-07-13 deploy added
  the **weekly charge cap** (`charge_grants` history table, `weekly_charge_cap`
  on `/api/spend`, enforced across all SKUs of a charge kind) on top of the
  **charge ledger** (`charges` table, `grant_charges` on `/api/spend`,
  `/api/charges/consume`, and `charges` + `owned_skus` + `weekly_usage` on
  `/api/state`). See [DEPLOYMENT.md](DEPLOYMENT.md).
- **Plugin version:** `5.23.0` (see [Plugin.cs:13](../valheim-plugin/Plugin.cs)).
  **Verified in-game and deployed 2026-08-31.**
  **5.22.2** makes the panel's text legible on the wood it now wears: 5.22.1 used
  the reference screenshot's colours literally, and mid-tone text on a mid-tone
  ground vanished. Body and secondary text are near-white, the accent styles draw
  a dark shadow behind themselves (`ShadowLabel`) the way the game outlines its
  TMP text, and the rate callout moved into a dark recess. Also: `PickFont`
  matched `AveriaSerifLibre-Bold` as a substring of `-BoldItalic`, so every
  heading, the primary button and the rate callout were rendering slanted.
  **5.22.1** fixes two faults in 5.22.0, both found by running it: a purchase
  against a server that predates the new verdict marker sat out the whole wait
  window and reported "Purchase Unconfirmed" for a spend that succeeded (the
  client now reads a plain reply as the verdict again, and takes the marker only
  when offered), and the panel came out orange — the modal scrim was sharing a
  style with the section rule and so tinted the whole screen, buttons were being
  brightened 1.5x, and the accent was read off an arbitrary label. The palette is
  now written down; sizes are still measured. New `panel_use_game_skin` key
  (default on) turns sprite extraction off.
  **5.22.0** (burned, never published) stops a purchase reporting failure while the coins are gone, and
  re-skins the panel from the game's own inventory screen. The client's
  give-up window was **12s** against a server that waits **15s** on the backend,
  so an ordinary Fly.io cold start painted "Purchase Failed" over a spend that
  then went through; an unanswered `/api/spend` is now retried on the same
  idempotency key (the only way to learn what really happened), `duplicate` is
  treated as "already paid, deliver it" instead of "skip the effect", an effect
  that cannot be delivered is **refunded** via `/api/admin/grant`, and the
  server states the verdict (`__BUYRES__:ok|fail|hold`) instead of the panel
  guessing it from the reply's wording. Gifts got the same retry.
  `ValheimTheme.cs` lifts the inventory's panel and button sprites, font, text
  colours and type sizes at runtime. Backend unaffected.
  **Built and deployed to HB Test; NOT yet verified in-game.**
  **5.21.2** stops a `period: once` quest reporting a reset it does not have
  ("already claimed today - resets in 7h 16m", printed from the daily timer for
  every quest alike). Server-side only. Diagnosed alongside a `vc_welcome` payout
  that appeared to pay nothing: it was **not** a bug and **not** the daily cap -
  three players claimed that one-time quest while it was priced at **30**, before
  the payout was raised to 300, and a one-time claim cannot be re-opened. The cap
  applies to `period: daily` quests only, verified against the ledger (300 paid in
  full on a day the same player's dailies were trimmed to exactly 8).
  **5.21.1** ends the two-ledger split that let the shop refuse a player holding
  **17,272** Valcoins by telling them they had **2**. `coin_balances.json` is a
  cache that accumulates deltas from an assumed zero and had not been written in
  8 days; the spend paths were gating on it. The local balance veto is gone from
  the shop, gifts and the ecosystem wallet (the backend answers 402 and its detail
  is surfaced), `admin_give`/`admin_remove` now go through `/api/admin/grant` and
  `/api/spend` instead of editing the cache, and `GrantPoller` reconciles each
  affected player's balance from `/api/state` after every ack. Backend unaffected.
  **Diagnosed against the live server; the code fix is NOT yet verified in-game.**
  **5.21.0** adds a **"Donations" button to the player inventory screen**
  (`InventoryMenuButton.cs`) — a clone of the vanilla "Take All" button, parked one
  row below Lost Scrolls II's "Rankings" button by reading that button's live
  anchors each frame (matched by GameObject name; no assembly reference, and it
  stands alone at the top centre when that mod is absent). Until now F4 was the only
  way in, and F4 is the only input path the system has. New config key
  `inventory_button_enabled` (default on); three new `libs/` DLLs
  (`UnityEngine.UI`, `UnityEngine.UIModule`, `Unity.TextMeshPro`). Backend
  unaffected. **Verified in-game 2026-08-25 on the HB Test profile: the button
  lands under Rankings and the click-through opens the panel.** Deployed to the
  HB Test profile **and the local dedicated server** (the latter an explicit
  one-off promotion, on request). 5.21.0 was never uploaded under its own
  number; **the inventory button reached players inside 5.22.2 on 2026-08-31**.
  Two notes from this release that outlived it: `deploy.ps1` was believed to
  target the test profile only, which the Gale hard-link finding later disproved
  (see the top of this file), and deploys copy the DLL and never the package
  metadata, so a deployed `manifest.json` reports whatever version was last
  copied by hand — that is why the dedicated server's was bumped in place for
  5.22.2 rather than left to drift.
  **5.20.0** adds the **ecosystem wallet API** (`ValcoinWallet` — the first
  sanctioned way for a sibling mod to *debit* Valcoins, used by Lost Scrolls II's
  wagered tournaments and duel invites) and an **uncapped quest flag**
  (`capped: false` in `valcoin_quests.yaml`, needs backend **0.10.0**) so event
  prizes are not trimmed by the 8-coin daily allowance. **Deployed to the
  dedicated server + both test profiles, and PUBLISHED to Thunderstore on
  2026-08-23** (verified against the Thunderstore API 2026-08-25 — this line
  previously claimed it was never uploaded, which was wrong). It was the public
  version until **5.22.2 superseded it on 2026-08-31**; the backend change went
  to Fly.io the same day it shipped (see the backend section above).
  **5.19.3** is cosmetic only: familiars hover at the player's **left** shoulder
  (`ArmorVfx.CompanionOffset` X `-0.75`), and the Fallen Valkyrie's smoke is
  stripped in favour of the Wraith's glow, grafted via the new
  `GlowFromPrefab`/`GraftGlow` path. **Verified in-game: the left-side move and
  the smoke removal. Not yet eyeballed: which Wraith effect variant the glow
  settles on** — the graft is pinned to `evil_smoke _local` after a first attempt
  cloned the Wraith's whole `Visual` root onto her. Backend unaffected.
  **5.19.2** fixes two coin-integrity bugs: the shop/gift pre-check treated a
  player missing from the local cache as having 0 coins and refused them their
  own money, and a failed balance-file write still acked the grant to the backend
  (silent loss on the next restart). Backend **0.8.0** adds the admin ledger —
  `GET /admin/ledger` (HTML) and `/api/admin/ledger` (JSON), the first read
  surface for grant history.
  **5.19.1** stops the Donate tab hanging on "Requesting your code..." forever
  when the server never replies — it now times out after 20s, explains itself and
  re-enables the button. **Open:** a live report of exactly that hang is *not*
  fixed by this; the action reaches the dedicated server and the backend returns
  200, but no reply comes back. Check the dedicated server's plugin version —
  `e9dc078` records the same shape when that box lags the client.
  **5.19.0** is panel copy only: the Donate tab no longer offers PayPal, and its
  step 4 no longer implies your claim code might already be filled in — the last
  echo of the assumption behind the backend 0.7.1 Ko-fi bug. It now states the
  real per-provider rule (paste on Ko-fi, automatic on GCash/Maya, one-time link
  on Patreon; the panel had never mentioned that Patreon step at all). It also
  carries everything in 5.18.0, which was staged but never uploaded.
  **5.18.0** adds **ServerGuide quest rewards** — a one-time "Patron's Welcome"
  worth 30 Valcoins and a daily pool capped at 8/day, with a 7-day streak bonus.
  ServerGuide is **unmodified**: quests set a `VC.Q.*` player key via its stock
  `set_player_key` reward, `QuestWatcher` reports it, and the backend decides
  whether it pays. Needs backend **≥ 0.7.0** (see above — not deployed yet).
  Quest content lives in
  [`examples/guidance.valcoin-quests.yaml`](../valheim-plugin/examples/guidance.valcoin-quests.yaml),
  which must be copied to the server's `BepInEx/config/ValheimServerGuide/`.
  **Deployed and verified working in-game 2026-08-08** — test profile *and* the
  dedicated server (DLL + `guidance.valcoin-quests.yaml` + `valcoin_quests.yaml`).
  Quests fire, coins credit, the daily cap and the F4 status line behave.
  Thunderstore package **5.19.0 zipped, not uploaded** (see the zip name in the
  release section below). It ships 5.18.0's quests and 5.17.0's familiar fix
  too, since neither was ever packaged. **5.18.0's number was burned, not
  reused** — that DLL is already running on the dedicated server and the test
  profile, so republishing different bytes under it would make "5.18.0"
  ambiguous.
  **Promote the ServerGuide YAML and the plugin DLL together** — shipping the
  YAML alone is what broke the first test (quest fired, reward silently didn't;
  see [OPERATIONS.md](OPERATIONS.md)). The dedicated server is not a
  `deploy.ps1` target and its plugin folder needs an **elevated** shell.
  **5.17.0** makes **Familiars survive an armor upgrade** (the aura lives on the
  item instance, and Valheim's upgrade path mints a *new* `ItemData` — it is now
  carried across, and the piece is re-equipped so the familiar comes straight
  back) and **shows the familiar on the crafting panel's Upgrade view** before
  you spend the materials. Verified in-game 2026-08-07. Backend unchanged.
  **As of 2026-08-07 `deploy.ps1` deploys to ONE destination only — the test
  profile, now `HB Test` in **Gale** (`%APPDATA%\com.kesomannen.gale\valheim\
  profiles\`); it was `Hearthbound Valheim - Test` in r2modman until the
  2026-08-17 move.** The played profile
  (renamed again, now **`HB Server`**) and the dedicated server are no longer
  deploy targets; promoting a tested build to them is a deliberate manual step.
  A missing target folder now **throws** instead of printing `SKIP` — see
  [OPERATIONS.md](OPERATIONS.md) for the two debugging sessions the silent-skip
  behaviour cost. **Restart the client to load a freshly deployed DLL.**
  **5.16.0** adds **shop preview images** (optional `preview_image` per SKU —
  `https` URL or a path relative to `BepInEx/config`; loaded async and cached by
  [ImageCache.cs](../valheim-plugin/ImageCache.cs)), a **click-to-enlarge zoom
  overlay** (fits to 80% of the window, never upscales past 1:1, closes on
  Close / click-outside / Escape, and outranks the other modals so it can be
  opened from the buy dialog), and the **`$1 USD = N Valcoins` callout** on the
  Donate tab fed by the backend's new `coins_per_usd`. The live Familiars
  catalog sets `preview_image` for all 8 SKUs. (5.7.0 = Soulkeeper Charm
  Phases 1+2; 5.8.0 = grouped/categorized Shop tab; 5.9.0 = native Valheim-style
  panel skin + Yes/Cancel purchase-confirm modal; 5.10.0 = `armor_vfx` auras;
  5.11–5.12 iterated slot-bound auras; 5.13.0 pivoted the category to
  **Familiars** — 8 mini flying-creature companions (Bat / Ghost / Deathsquito /
  Drake / Wraith / Volture / Gjall / Fallen Valkyrie) hovering at the right
  shoulder, bound to the equipped helmet, tier-priced 400–1300c; whole-creature
  visuals cloned inside an inactive holder, stripped of AI/network/physics.
  **5.14.0** fixed the familiar clones (dependency-ordered strip so no more
  "Can't remove Humanoid" log; `flying` animator bool so the Hatchling animates;
  height/particle tuning; spawn/despawn poof) and added **feather fall** (shared
  with the Feather Cape). **5.15.0** adds a **small flat attack bonus per
  familiar** (`SE_FamiliarBond` on the game's `ModifyAttack`: +2/+3 of the
  creature's damage type), an **overwrite warning** in the buy modal when the
  helmet already has a familiar, **Gjall drips removed**, the **10-charge/week
  Soulkeeper cap**, and a **tomb-area creature repel** on Valkyrie landing.
  Server's `valcoin_shop.yaml` still has 20 SKUs — restart to reload. NOTE:
  server DLL copies require the dedicated server to be STOPPED first.)
- **Backend tests:** 65 passing (`cd backend; pytest`) — includes 3 new weekly
  charge-cap tests. `pytest` is not in the base Python env — install
  `requirements-dev.txt` in a venv first (see [DEVELOPMENT.md](DEVELOPMENT.md)).
- **Last code activity:** 2026-07-19 — shop **preview images** + **click-to-
  enlarge** overlay, **exchange-rate callout** on the Donate tab, backend
  `coins_per_usd` on `/api/state`; plugin bumped to 5.16.0 and backend to 0.6.0,
  both deployed. Added a `UnityEngine.UnityWebRequestTextureModule` reference to
  the csproj (copied into `libs/` from the Steam install) — required for
  `UnityWebRequestTexture`/`DownloadHandlerTexture`.
- **Deployment target:** Fly.io, region `sin` (Singapore), 256 MB shared VM,
  1 GB persistent volume for SQLite. **Live** — see [DEPLOYMENT.md](DEPLOYMENT.md).

## Known discrepancies

### Preview images use config-relative paths — remote clients see blanks (2026-07-19)

The live `valcoin_shop.yaml` (identical on the dedicated server and the test
profile) sets `preview_image` for all 8 Familiars as **config-relative paths**
(`shop_images/Bat.png`, …), and the 8 PNGs exist in `BepInEx/config/shop_images/`
in **both** those locations. But the catalog RPC syncs the *string*, not the
file: a connecting player resolves `shop_images/Bat.png` against **their own**
`BepInEx/config` and — having no such folder — gets a blank space where the
thumbnail should be. Only this machine renders them.

**Fix:** host the 8 PNGs at `https` URLs and point `preview_image` at those.
Worth downscaling first — the sources are 300–780 KB each (~3.5 MB total) for
images that render at 72px in the shop row, so every client would pay that
download for nothing.

### `deploy.ps1` targets the wrong profiles — real play profile is missed (2026-07-20)

`deploy.ps1` lists a `Hearthbound Valheim` (non-test) r2modman profile that
**does not exist**; every deploy prints `SKIP (missing folder)` for it and
copies to only the Test profile + dedicated server. The actual profiles on this
machine are: `Default`, `Hearthbound Valheim - Test`, **`Heathbound Server`**
(note the typo — missing `r`), `Mod Test Profile`, `_snapshots`.

**`Heathbound Server` is the profile actually being played**, and the script
doesn't target it — so it never received a configured `valcoin_config.json`.
On 2026-07-20 this surfaced as a persistent **Offline** panel: the profile ran
the correct 5.16.0 DLL but its config was still the placeholder template
(`your-app.fly.dev` / `paste-the-…`), so `Backend ready: False`. Fixed by copying
the working `backend_url` + `plugin_token` from the Test profile into it
(`.bak` left beside it); **requires a game restart** to take effect.

**Resolved 2026-07-20:** the profile was renamed `Heathbound Server` →
**`Hearthbound Server`** (typo fixed), `deploy.ps1`'s dead `Hearthbound Valheim`
entry was repointed at it, and the script now prints a **placeholder-config
warning** per profile at deploy time (it copies the DLL only, so a current DLL
+ template config — the exact failure here — otherwise goes unnoticed until
in-game). A verified `deploy.ps1 -NoBuild` now copies to all three real
destinations with no `SKIP` and no warning. See
[OPERATIONS.md](OPERATIONS.md#offline-panel-check-the-right-profile-first).

### Soulkeeper Charm added; cosmetic perks + chat decoration removed (2026-07-12)

The shop's cosmetic `grant_perk` perks — `donor_badge`, `chat_title`,
`companion_flair`, `lordslayer_title` — are **removed from the catalog and
code**, and `ChatDecoration.cs` is **deleted**. On this dedicated-server build
their client-side rendering was unreliable (peer-to-peer chat routing, no
`NetworkUserId`), so repeated "badge/title still not working" reports were
architectural, not a bug to chase.

They're replaced by the **Soulkeeper Charm** consumable (`add_charges` effect):

- **Phase 1 (live):** buying credits a backend `charges` pool; on the local
  player's death one charge is consumed to **skip the skill drain**
  (`Soulkeeper.cs`, backed by `/api/charges/consume`). 3 decoy-priced tiers.
- **Phase 2 (prototype, needs live play-testing):** the same warded death also
  makes the intro Valkyrie **carry the player from the spawn point to their
  tombstone** on respawn (`ValkyrieCarry.cs`) — fade transition, ESC-menu +
  auto-pickup suppressed mid-flight, watchdog + plain-teleport fallback. First
  live test flew end-to-end; a post-flight `AutoPickup`/`FloatingTerrain` NRE
  was addressed by suspending auto-pickup during the carry (watch for recurrence).

Also this pass: shop UI now renders owned / weekly-cap / charge states from
`/api/state` (the client was previously blind to server-side ownership), all
game input is blocked while the panel is open, fonts enlarged, and the donor
portal is a single-column provider list (Ko-fi → PayMongo → Patreon → PayPal
with logos). Catalog is **20 SKUs** (3 Soulkeeper tiers + 8 Familiars + 9 `grant_item`
bundles). See [SHOP.md](SHOP.md).

> **Follow-up (2026-07-13, plugin 5.14–5.15, backend redeployed):** Familiars
> gained light utility — each grants **feather fall** (the Feather Cape's own
> `SlowFall` effect, non-stacking) and a **small flat attack bonus**
> (`SE_FamiliarBond`, +2/+3 of the creature's damage type via the game's
> `ModifyAttack`; 1–4 % of endgame weapon damage — inside the balance rule). The
> buy modal now **warns before overwriting** a familiar already on the equipped
> helmet. The Soulkeeper pool is capped at **10 charges/player/week** (shared
> across the ×1/×5/×10 tiers), enforced backend-side from a new `charge_grants`
> table via `weekly_charge_cap`. On Valkyrie landing, three shockwave pulses
> **repel hostile creatures** from the tombstone (zero-damage, no-attacker
> pushback — no aggro). Familiar-clone fixes: dependency-ordered component strip
> (no more "Can't remove Humanoid"), `flying` animator bool (Hatchling animates),
> Gjall tar drips removed, particle sizes tamed, spawn/despawn poof.

### F4 Codex + F8 panel merged into one panel (2026-07-09)

`DonationCodex.cs` is deleted; `DonationPanel.cs` is now the single combined
panel, opened with **F4** (`codex_toggle_key`). The F8 hotkey was removed
entirely per user request (`ui_toggle_key` is now unused). Tabs: Donate,
Shop, Gift, Patrons, Admin. The Donate tab shows the code inline (Copy button
+ Open-portal button via `Application.OpenURL`), enforces a 30s client-side
cooldown, and has a Terms of Use modal. All emoji were removed from the UI and
every server reply string — Valheim's IMGUI font renders emoji as blank
squares. Donate replies now use a structured `__DONATE__:code|url|ttl` /
`__DONATE_ERR__:msg` wire format (see `Flows.cs` /
`DonationPanel.OnServerMessage`).

### Which client profile is "live" keeps drifting — recurring Offline cause

**This has now bitten twice**, and the earlier fix was based on a wrong profile
name. History:

- The live client does **not** run `Hearthbound Valheim - Test`. An earlier pass
  believed it ran `Hearthbound Valheim` and pointed `deploy.ps1` there — but no
  such profile exists on this machine (it `SKIP`s).
- As of 2026-07-20 the played profile is confirmed to be **`Heathbound Server`**
  (typo, missing `r`), which `deploy.ps1` does not target at all. It ran the
  right DLL but a placeholder `valcoin_config.json`, so the panel showed
  **Offline** — see the dated discrepancy above.

**Whenever Offline recurs, don't guess the profile — measure it.** The freshest
`BepInEx/LogOutput.log` across all profiles is the one being launched; then read
that log's `[Valcoin]` lines (they name the exact reason) and check its
`valcoin_config.json` for placeholder values. Full procedure in
[OPERATIONS.md](OPERATIONS.md#offline-panel-check-the-right-profile-first). The
durable fix is to make `deploy.ps1`'s destination list match the profile
actually played (or rename the profile to match the list).

### Chat/console commands removed — BREAKING (2026-07-09)

`ChatSlashPatch.cs` is deleted. All donation actions (`/donate`, `/coins`,
`/shop`, `/buy`, `/gift`, `/topdonors`, `/title`, `/givecoins`,
`/removecoins`) now exist **only** as F4 Codex / F8 panel buttons over the
`vc_action` RPC — there is no chat or console fallback. Reason: the
reflection-based `Chat.RPC_ChatMessage` hook was unreliable alongside other
chat-patching mods on this server (root cause not fully diagnosed — `/donate`
silently did nothing, no errors logged).

> **Update (2026-07-12):** `ChatDecoration.cs` (the donor-badge/chat-title chat
> prefix) has since been **deleted** — see the Soulkeeper Charm entry below.

**Consequence:** the plugin is now required **client-side**, not just
server-side — a vanilla client can no longer use the donation system at all.
On this server that's moot (ServerGuard already kicks vanilla clients), but
it's a real behavior change for the public Thunderstore listing. See
[SHOP.md](SHOP.md#no-chat-or-console-commands).

New: an **Admin tab** in the F8 panel (give/remove balance) replaces the
removed `/givecoins`/`/removecoins`, gated on a `whoami` RPC check against
`valcoin_admins.yaml`.

### Remote/vanilla clients need the catalog broadcast to arrive — RESOLVED (2026-07-08)

`Catalog.cs` only ever loaded `valcoin_shop.yaml` from whichever machine had
the file (the dedicated server). Remote clients — including vanilla ones —
saw an empty shop. `CatalogSync.cs` now broadcasts the parsed catalog to every
connected client every 30s over a new `vc_catalog` RPC
(`Catalog.Serialize()` / `Catalog.ApplyRemote()`, in-memory only, never
written to the remote client's disk). See [PLUGIN.md](PLUGIN.md).

### Build artifact mismatch — benign ⚠

The plugin's compiled output in
[bin/Release/](../valheim-plugin/bin/Release) still contains stale
`Jotunn.dll` and `YamlDotNet.dll` copies, even though the current
[csproj](../valheim-plugin/ValheimDonationSystem.csproj) no longer references
them (both deps were dropped). They're left over from before the change and an
incremental build doesn't delete them. **This is harmless for deploys:**
`deploy.ps1` copies only `ValheimDonationSystem.dll`, not the folder, so the
stale DLLs never ship. When **packaging for Thunderstore**, do a clean build
(delete `bin/`) or hand-pick the DLL so the zip doesn't bundle them — see
[THUNDERSTORE.md](THUNDERSTORE.md).

### Unity DLLs / reference set — RESOLVED (2026-07-04)

[libs/](../valheim-plugin/libs) is now a **version-consistent set** copied from
the current dedicated-server `valheim_server_Data/Managed/` (assembly_valheim +
UnityEngine + CoreModule + IMGUIModule + InputLegacyModule +
UnityWebRequestModule + **TextRenderingModule**, the last now referenced in the
csproj for `FontStyle`). The plugin builds clean against the **current** Valheim
(Ashlands) — `dotnet build -c Release` → `bin/Release/ValheimDonationSystem.dll`.

To build against a different Valheim version, replace the whole libs set from one
install at once (mixing versions causes cascade errors like `ZDOMan.GetMyID`
missing or the `BaseUnityPlugin.Config` name collision).

### Removed commands pruned (2026-07-04)

`/sethome`, `/home`, `/shout` (and the `sethome` / `shout` perks) are **removed
from code** — the handlers were deleted from `ChatSlashPatch.cs` and
`UiActionRouter.cs`, and the F8 panel's shout editor removed. `PerkManager` still
carries the now-unused home/charge helpers (harmless; prune later if desired).

### Placeholder Fly app name — RESOLVED (2026-07-05)

The placeholder name `valheim-donations` turned out to be unclaimed, so
`flyctl launch --no-deploy` kept it as-is — the live app is
`https://valheim-donations.fly.dev`. See [DEPLOYMENT.md](DEPLOYMENT.md).

### Provider rollout — all four providers live (2026-07-10)

Live on Fly secrets: **Ko-fi**, **Patreon**, **PayPal**, and **PayMongo**. Each
webhook returns **401** to a bad-signature probe (configured & verifying) rather
than **503** (unconfigured). Full secret list in
[DEPLOYMENT.md](DEPLOYMENT.md#live-status-2026-07-10); per-provider setup in
[PROVIDERS.md](PROVIDERS.md).

Per-provider notes worth remembering:

- **Ko-fi** — the only provider tested end-to-end with a (synthetic) webhook
  through to in-game delivery. Code rides in the message field.
- **Patreon** — payments carry no claim code, so a first-time patron must click
  **"Link my Patreon account"** on the portal once (OAuth); renewals auto-credit
  thereafter via `provider_links`.
- **PayPal** — **removed 2026-08-11.** Auto-credit required a PayPal *business*
  account (for `PAYPAL_BUSINESS_EMAIL`), which this server doesn't have; the
  PayPal.Me fallback could never auto-credit at all. All `PAYPAL_*` secrets are
  unset, so the card doesn't render and `/webhooks/paypal` returns 503. The
  handler stays in the codebase, dormant. This also retires the hosted-button
  risk noted here previously.
- **PayMongo** — the tightest flow: the portal mints a PaymentLink server-side
  with `metadata.claim_code` baked in, so the code is guaranteed to travel. Live
  `sk_live` key verified by minting a real (unpaid) ₱100 PaymentLink. Covers
  GCash + Maya + GrabPay + cards in one integration; priced in PHP.
  **Bug fixed during rollout (backend `0a69502`):** `_provider_links` set
  `out["paymongo"] = {}`, but the template gates the card on
  `{% if providers.paymongo %}` and an empty dict is falsy in Jinja, so the card
  stayed hidden whenever PayMongo was configured — now `{"enabled": True}`.

**Live-money testing was deliberately skipped** for PayMongo per user decision
(2026-07-10). It is verified configured (401 probe, live PaymentLink mint) but
**no real charge has ever flowed through it** — so unlike Ko-fi and Patreon, its
webhook registration has never been proven by a real delivery. One ₱20 donation
would close that gap; see the QA matrix below.

### QA sweep — every donation path (2026-08-11)

Full-chain harness ([`qa_donation_paths.py`](../backend/qa_donation_paths.py)):
claim mint → signed webhook → grant → `/api/grants/pending` → ack → balance,
**44/44 passing**. Covers all three live providers, both recovery paths, forged
signatures, duplicate webhooks, code reuse, and ledger invariants.

Two routing rules the sweep pinned down, both worth knowing:

- A **valid claim code outranks the donor's email link**, so donating on a
  friend's behalf credits the friend.
- A **stale (expired/used) code plus an already-linked email routes to the
  linked account.** Right for repeat donors, but it means a gift attempted with
  an expired code silently pays the donor instead of the recipient.

What the harness cannot reach: the providers' own servers, and the C# plugin
applying a delivered grant in-game. Both need a real donation to verify.

## Regenerating this snapshot

Most of the facts above are also computed programmatically by
[scripts/generate_setup_guide.py](../scripts/generate_setup_guide.py) when
it builds [SETUP_GUIDE.pdf](SETUP_GUIDE.pdf). If this file and the PDF ever
disagree, trust the PDF (or re-run the script) — this file is maintained by
hand.
