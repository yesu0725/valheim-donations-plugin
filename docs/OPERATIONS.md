# Operations

Runtime behavior, safety nets, and troubleshooting for a live deployment.

## Idempotency & safety nets

- SQLite + WAL on a 1 GB Fly volume comfortably handles thousands of donations.
- All webhooks are idempotent via `donations(provider, provider_txn_id)`
  UNIQUE — provider retries can't double-credit.
- The grant pipe is doubly idempotent: the backend tracks ack state per
  grant, and the plugin's `CoinManager` separately caches the last 5000
  applied grant ids locally, so crash-then-replay on either side won't
  double-credit.
- `/api/spend` and `/api/transfer` are idempotency-keyed by the plugin, so
  retries on flaky networks are safe.
- **`coin_balances.json` is a cache, not a ledger.** It holds applied-grant ids
  (the dedupe above) and a balance per player, but that balance is only ever a
  running total of what this server happened to observe — it starts at 0 for a
  player it has never recorded, so a drifted or fresh cache can report a balance
  that is wildly wrong. Since 5.21.1 nothing gates a spend on it and `GrantPoller`
  overwrites it from `/api/state` after each ack, but if you are reading it by
  hand: the backend is the answer, this file is a guess. Before 5.21.1 the shop
  gated on it, which refused a player with 17,272 coins on the grounds that this
  file said 2.
- OAuth states have a 10-minute TTL; expired rows are GC'd opportunistically.
- Donations under `MIN_GRANT_COINS` are recorded as `rejected` for audit but
  not credited.

## Unmatched donations

If a donation arrives without a recognizable claim code (donor forgot to
paste it, or pasted it somewhere the provider stripped), it's stored but
left unlinked to a Steam64.

- `GET /api/admin/unmatched` — list donations awaiting manual reconciliation.
- `POST /api/admin/credit-unmatched` — manually bind one to a Steam64 and
  credit it retroactively.
- `POST /api/admin/links` — bind a provider account ↔ Steam64 so future
  donations from that same account auto-match.
- `POST /api/admin/grant` — free-form coin adjustment, for support cases
  that don't fit the above.

## The plugin's local balance cache is not authoritative

`BepInEx/config/valcoin_data/coin_balances.json` on the **server** is a cache.
The backend's SQLite is always the source of truth, and the F4 panel reads the
backend directly (`/api/state`), never this file.

It has been observed **wildly** wrong, so do not diagnose from it:

- It only ever learns a balance by ADDING a delta to what it already holds, and
  it holds **0** for a player it has never recorded — so its first write for an
  unknown player equals that one grant, not their balance.
- Individual grants have gone missing from it while landing correctly in the
  ledger (one player read 15 against a true 315 — its `recentGrants` held the
  grant ids either side of the 300 and skipped it).

Since **5.21.1** nothing gates a spend on it, and `GrantPoller.Reconcile`
overwrites a player's entry from `/api/state` after each grant batch. Note the
reconcile only fires for players who had a grant in that batch, so an idle
player's entry can stay stale indefinitely — harmless, but don't read it.

## Reading the live ledger

The fastest way to answer "does this player actually have the coins?" — and the
only reliable one, since the cache above lies and `LogOutput.log` is wiped on
every restart.

The token is `plugin_token` from the server's
`BepInEx/config/valcoin_config.json`. The header is **`Authorization: Bearer
<token>`** — `X-Plugin-Token` returns 401.

```powershell
$cfg = Get-Content "<server>/BepInEx/config/valcoin_config.json" -Raw | ConvertFrom-Json
$h = @{ Authorization = "Bearer $($cfg.plugin_token)"; Accept = 'application/json' }

# one player's authoritative balance
Invoke-RestMethod -Uri "$($cfg.backend_url)/api/state/<steam64>" -Headers $h

# their full history — note include_hidden, or the owner's own account is omitted
$j = (Invoke-WebRequest -Headers $h -Uri `
  "$($cfg.backend_url)/api/admin/ledger?steam64=<steam64>&since=30d&limit=200&include_hidden=true").Content | ConvertFrom-Json
$j.entries | Format-Table -AutoSize

# anything still awaiting delivery (empty = nothing is in limbo)
Invoke-RestMethod -Uri "$($cfg.backend_url)/api/grants/pending?limit=50" -Headers $h
```

The ledger response is `{ summary, entries }` — the rows are under `entries`,
not at the top level. A player's `entries` (excluding `donation` rows, which
show fiat) should sum to their `/api/state` balance; if it does, the ledger is
consistent and any disagreement is on the plugin side.

## Common errors

| Symptom | Likely cause |
|---|---|
| 503 from a `/webhooks/*` route | That provider's env vars are unset on the backend. Set them and redeploy — see [PROVIDERS.md](PROVIDERS.md). |
| Panel shows **Offline** / plugin logs `Backend ready: False` | `backend_url` or `plugin_token` in `valcoin_config.json` is still the **placeholder** (`your-app.fly.dev` / `paste-the-…`), wrong, or the backend isn't reachable. The plugin deliberately treats the template values as not-configured. **Read the log** — it names the exact reason. See "Offline panel: check the right profile" below. |
| Plugin builds but won't load | A Unity DLL is missing from `libs/` — see [PLUGIN.md](PLUGIN.md)'s DLL table. |
| Donor paid but no in-game credit | Check the unmatched list first. If the donation isn't there at all, the webhook didn't fire — most provider dashboards have a manual retry/redeliver button. |
| Buying a consumable says "cap reached" | Expected once a `grant_item` SKU's **weekly cap** is hit for that player — resets on the weekly boundary (recommend Monday 00:00 server time). Enforced backend-side on `/api/spend`. |
| Buying a food/mead SKU is refused | The SKU's `requires_boss` gate isn't satisfied — the player/world hasn't defeated the gating boss yet. |
| Coins debited but consumable not received | A `grant_item` SKU with a **wrong prefab id** (e.g. an unverified Ashlands food) charges and gives nothing. Verify prefab ids against your Valheim version. Since 5.22.0 this case **refunds automatically** and the panel says so. |
| Shop works, then after **logging out and back in** (game not closed) coins are taken and nothing arrives — no confirmation, no aura, no refund, quests stop paying | **Client is older than 5.22.3.** RPC handlers were registered once per process while the game rebuilds `ZRoutedRpc` per session, so every session after the first had no handlers: actions reached the server (debited) and every reply was dropped, including the one that triggers the refund. Fixed in 5.22.3. Look for a second `[Valcoin] RPC registered (client)` line on relog — its absence is the diagnosis. |
| **Hosting a local world**: nothing in the panel does anything, no error | **Client is older than 5.22.4.** The host is not in the server's own peer list, so the router dropped every host action silently. Fixed in 5.22.4; an unresolvable sender now logs `[Valcoin] action from unknown peer`. Note ordinary players still cannot buy from a local world — they have no `plugin_token` — see [SHOP.md](SHOP.md#buying-from-a-locally-hosted-world). |
| On **Valheim 1.0**, the familiar name is missing from the helmet ("…of the Bat"); log says `GetTooltip not found — armor rename disabled` | **Client is older than 5.23.1.** 1.0 added a parameter to `ItemData.GetTooltip` and the exact-signature lookup stopped matching. Everything else about the familiar keeps working. Fixed in 5.23.1 (matches by shape; logs the bound signature at startup). |
| On **Valheim 1.0**, BepInEx does not come up at all, no `[Valcoin]` lines in the log | The profile's **BepInExPack** predates 1.0. The pack known to run on 1.0.7 is `denikson-BepInExPack_Valheim-5.4.2350`; read the installed version from Gale's own list (or its `data.sqlite3`), **not** from an old `LogOutput.log`. |
| A familiar sits somewhere odd on **your** screen only | `BepInEx/config/valcoin_familiars.yaml` on your client — it is per-client and cosmetic. Delete the entry to restore the default; the file is re-read within a second. |
| *(removed)* `/sethome` / `/home` / `/shout` | These commands + their perks were removed by design decision — see [SHOP.md](../docs/SHOP.md). |
| *(removed)* all chat/console commands | `/donate`, `/buy`, `/gift`, `/coins`, `/topdonors`, `/title`, `/givecoins`, `/removecoins` are all gone — everything is the in-game panel (F4) only now. See [SHOP.md](../docs/SHOP.md#no-chat-or-console-commands). |

## Offline panel: check the *right* profile first

When the in-game panel shows **Offline**, the backend is usually fine — the
cause is almost always the **client config on the profile actually being
played**. A worked example (2026-07-20):

- The live backend was healthy: `curl https://valheim-donations.fly.dev/` → 200,
  and `/api/state/<id>` with the real token → 200. Not the problem.
- The **plugin logs are the fastest diagnosis.** The client's
  `BepInEx/LogOutput.log` had, verbatim:
  `[Valcoin] valcoin_config.json still has the PLACEHOLDER backend_url
  (your-app.fly.dev). … Donation actions are disabled until then.` →
  `Backend ready: False`. That one line is the whole diagnosis.
- **Root cause:** the machine has several r2modman profiles, and the one being
  played (then named `Heathbound Server` — note the typo, missing an `r`) had the
  untouched **template** `valcoin_config.json`. `deploy.ps1` didn't target it, so
  it got the DLL by hand at some point but was never configured.
- **Resolved:** the profile was renamed to `Hearthbound Server`, `deploy.ps1`
  was repointed at it, and the script now emits a placeholder-config warning at
  deploy time (see the gotcha below). This class of bug had bitten twice before
  it was caught here.

**How to find the profile that's actually running** (the mod manager rewrites
`LogOutput.log` on each modded launch, so the freshest log wins):

```bash
base="$APPDATA/com.kesomannen.gale/valheim/profiles"
for p in "$base"/*/; do
  echo "$(stat -c '%y' "$p/BepInEx/LogOutput.log" 2>/dev/null)  $(basename "$p")"
done | sort   # newest = the profile you're playing
```

Profiles moved from r2modman (`$APPDATA/r2modmanPlus-local/Valheim/profiles`)
to **Gale** on 2026-08-17; check the old path too if a profile seems to have
vanished.

Then check that profile's `BepInEx/config/valcoin_config.json` for placeholder
values, and **restart the game** after fixing it — the plugin reads config once
at startup (`Config.Load()` in `Awake`), so a live session won't pick up an
edit.

> **Gotcha:** a profile name typo (`Heathbound` vs `Hearthbound`) is exactly how
> a profile slips past `deploy.ps1`'s target list. If you play from a profile
> the script doesn't list, every rebuild silently leaves it on an old DLL and
> its config untouched.
>
> **Changed 2026-08-07 — this class of failure is now fatal, not silent.**
> `deploy.ps1` deploys to **one** destination, the test profile (`HB Test` in
> Gale; was `Hearthbound Valheim - Test` in r2modman until 2026-08-17), and
> **throws** if that folder is missing
> instead of printing `SKIP (missing folder)` and continuing. The live/played
> profile and the dedicated server are deliberately **not** deploy targets any
> more — promoting a tested build to them is a manual step. The old
> warn-and-continue behaviour cost two debugging sessions: the played profile
> was renamed (`Hearthbound Valheim` → `Hearthbound Server` → `HB Server`),
> the deploy kept reporting success, and a plugin fix was twice judged "broken"
> when the profile under test was simply running a weeks-old DLL.
>
> **Before concluding a code fix didn't work in-game,** compare the deployed
> DLL's **SHA-256** against `valheim-plugin/bin/Release/ValheimDonationSystem.dll`
> -- timestamp and size are weaker signals, and a same-size DLL from the previous
> release is exactly what a stranded profile looks like. Check every Gale profile
> and the dedicated server, not just the one you think is under test.

> **Deploy rule (owner's instruction, 2026-08-07, reaffirmed 2026-09-18): a new
> build goes to the `HB Test` Gale profile and to NO other profile.** The played
> profiles and the dedicated server are promoted by hand after testing.
>
> **Gale hard-links a mod's files across profiles, which silently breaks that
> rule if you just copy.** Every profile with the same version installed shares
> one NTFS inode (measured 2026-08-31, 2026-09-01, 2026-09-09 and 2026-09-18 --
> which profiles share it changes as Gale re-links; on 2026-09-18 it was all
> five). `Copy-Item` overwrites a file's *contents*, so copying onto a linked
> file writes through the link into every profile sharing it. From the move to
> Gale on 2026-08-17 until 2026-09-18 that is what every deploy did, and from
> 2026-09-01 a verification pass made it worse by deliberately copying into any
> profile whose hash did not match. Both are gone.
>
> **`deploy.ps1` now deletes HB Test's DLL before copying.** Removing one link of
> a hard-linked file drops only that directory entry -- the other profiles keep
> their bytes -- and the copy that follows is HB Test's own file. Verified
> 2026-09-18 with `fsutil file queryfileid`: before the run all five profiles
> shared inode `...58936`; after it HB Test was on `...5b11d` and the other four
> were unchanged in both inode and hash. The script snapshots every other
> profile's hash before writing and reports `untouched` / `CHANGED!` per profile
> afterwards, so "nothing else moved" is demonstrated on every run, not assumed.
> Gale may re-link HB Test the next time it installs or updates the mod there;
> the next deploy simply breaks it again.
>
> **The report at the end is read-only.** It prints which build every profile is
> on because guessing that has cost debugging sessions, but it writes nothing.
> To put a build on a played profile, do it deliberately: Gale's own update
> flow, or a hand copy you chose to make. The dedicated server is standalone
> (not linked to anything) and stays a manual promotion.
>
> Measure it, never assume it:
>
> ```powershell
> foreach ($p in (Get-ChildItem "$env:APPDATA\com.kesomannen.gale\valheim\profiles")) {
>   fsutil file queryfileid "$($p.FullName)\BepInEx\plugins\TaegukGaming-Valheim_Donations\ValheimDonationSystem.dll"
> }
> ```

## Quest rewards: promote the ServerGuide YAML and the plugin DLL together

A quest is **two halves on two machines**. ServerGuide's YAML (server-side, and
it syncs the quest to every client) makes the quest *fire*; the donation
plugin's `QuestWatcher`/`QuestFlow` makes it *pay*. Ship one without the other
and the quest works perfectly while the reward silently doesn't.

**Worked example (2026-08-08).** The quest YAML was copied to the dedicated
server, but its `ValheimDonationSystem.dll` was still 5.17.0. Result: the quest
fired, the client reported it, and nothing paid.

The two log lines that gave the whole diagnosis:

- **Client** `LogOutput.log`: `[Valcoin] Quest 'vc_welcome' completed; reported
  to server.` → the client half was fine.
- **Server** `LogOutput.log`: **no `[Valcoin] Quest catalog loaded: N quest(s)`
  line at all.** That line only exists in 5.18.0+. Its absence *is* the
  diagnosis — the server had no quest handler, so `UiActionRouter` fell through
  to `default:` and answered `⚠️ Unknown UI action: quest`.

**Check the server log for `Quest catalog loaded` before debugging anything
else.** If it's missing, the server is on an old DLL; nothing downstream matters.

> **The dedicated server is not a `deploy.ps1` target** (see the gotcha above),
> so promoting to it is manual and easy to do by halves. Copy the DLL and the
> YAML in the same step, and remember the DLL lives under
> `C:\Program Files (x86)\…`, which needs an **elevated** shell — a non-elevated
> copy fails with a permission error that's easy to skim past.
>
> Restart **both** server and client afterwards: the plugin reads its config and
> catalogs once, in `Awake`.

### Why a failed payout is no longer lost

Same incident, second cause. `QuestWatcher` used to clear the `VC.Q.<id>` key
the moment it sent the report, on the reasoning that ZRoutedRpc is reliable.
That conflates *delivered* with *understood*: the old server received the report
and did nothing with it, the key was already gone, and `once: true` quests can
never fire again — so the completion was **unrecoverable, not merely unpaid**.
Upgrading the server afterwards could not make it good.

Since 5.18.0 the server sends an explicit `vc_questack` **only** once the backend
returns a definitive answer (credited / already claimed / capped), and the client
clears the key only on that ack. Everything else leaves the key in place and
re-reports every 60 s:

| Situation | Behaviour |
|---|---|
| Server on an old build (no quest handler) | No ack → retries → pays as soon as the server is upgraded |
| Backend unreachable | No ack → retries → pays when the backend returns |
| Quest id has no price in `valcoin_quests.yaml` | No ack → retries; server logs `Unknown quest '<id>'` every 60 s |

That repeating warning is deliberate — it's how a misconfiguration announces
itself instead of quietly eating rewards.

**Recovering a completion lost before this fix:** the key is gone and the entry
is marked fired, so re-running it needs ServerGuide state cleared first —
`vsg_reset_player <player> <entryId>` (e.g. `vc_welcome_haldor`), then redo the
quest. Alternatively credit directly with `/api/admin/grant`, but resetting is
better because it re-tests the whole path.

## Testing a daily without waiting for 00:00 UTC

`POST /api/admin/quest-reset` with `{"steam64": "...", "quest_id": "daily_hunt"}`
clears that claim so it can be earned again immediately; omit `quest_id` to clear
every quest for the player. It does **not** claw back coins already granted — use
`/api/admin/grant` with a negative amount for that.

Remember the ServerGuide side has its own state: a `once: false` daily re-fires
freely, but a `kill`-count daily needs its counter refilled (it clears on
completion, so just get the kills again).

## Verifying a change end-to-end

1. In-game, press **F4** to open the panel and click **Donate** — plugin should show a
   portal URL with a fresh code.
2. Open the URL — portal should show the code and four provider buttons.
3. Make a test donation (Ko-fi or PayPal sandbox is easiest).
4. Watch backend logs for the webhook hit and a new `grants` row.
5. Within `poll_interval_seconds` (default 10s), the plugin should pop a HUD
   message and the panel's balance should reflect the new total.
6. Exercise the Shop and Gift tabs to confirm `/api/spend` and `/api/transfer`
   are behaving.

For **quest rewards**, the equivalent pass (verified working 2026-08-08):

1. Server log shows `[Valcoin] Quest catalog loaded: N quest(s)` at startup. If
   it doesn't, stop — the server is on a pre-5.18.0 DLL.
2. Complete a quest in-game. Client log: `Quest '<id>' completed; reported to server.`
3. Server log: `quest '<id>' paid N to <player> (N/8 today)`.
4. Client log: `Quest '<id>' acknowledged by server; key cleared.` **If this
   line never appears**, the report wasn't accepted — the client will keep
   retrying every 60 s, and the server log says why.
5. Within `poll_interval_seconds`, the `+N Valcoins!` toast appears and the F4
   panel's daily line advances (`Daily quests: N/8 · resets in …`).
6. Re-complete the same daily: no coins, and the panel logs
   `already claimed today`.

## Source of truth

- [backend/app/routes/admin.py](../backend/app/routes/admin.py) — admin reconciliation endpoints
- [valheim-plugin/CoinManager.cs](../valheim-plugin/CoinManager.cs) — local cache + dedupe
- [valheim-plugin/GrantPoller.cs](../valheim-plugin/GrantPoller.cs) — poll loop
