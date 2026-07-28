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

It only answers the panel's balance display instantly without a network
round-trip. The backend's SQLite is always the source of truth. If the two
ever disagree (e.g. after a manual `/api/admin/grant`), the plugin's next
poll cycle reconciles it.

## Common errors

| Symptom | Likely cause |
|---|---|
| 503 from a `/webhooks/*` route | That provider's env vars are unset on the backend. Set them and redeploy — see [PROVIDERS.md](PROVIDERS.md). |
| Panel shows **Offline** / plugin logs `Backend ready: False` | `backend_url` or `plugin_token` in `valcoin_config.json` is still the **placeholder** (`your-app.fly.dev` / `paste-the-…`), wrong, or the backend isn't reachable. The plugin deliberately treats the template values as not-configured. **Read the log** — it names the exact reason. See "Offline panel: check the right profile" below. |
| Plugin builds but won't load | A Unity DLL is missing from `libs/` — see [PLUGIN.md](PLUGIN.md)'s DLL table. |
| Donor paid but no in-game credit | Check the unmatched list first. If the donation isn't there at all, the webhook didn't fire — most provider dashboards have a manual retry/redeliver button. |
| Buying a consumable says "cap reached" | Expected once a `grant_item` SKU's **weekly cap** is hit for that player — resets on the weekly boundary (recommend Monday 00:00 server time). Enforced backend-side on `/api/spend`. |
| Buying a food/mead SKU is refused | The SKU's `requires_boss` gate isn't satisfied — the player/world hasn't defeated the gating boss yet. |
| Coins debited but consumable not received | A `grant_item` SKU with a **wrong prefab id** (e.g. an unverified Ashlands food) charges and gives nothing. Verify prefab ids against your Valheim version. |
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
  played (`Heathbound Server` — note the typo, missing an `r`) had the untouched
  **template** `valcoin_config.json`. `deploy.ps1` only targets
  `Hearthbound Valheim - Test` and the dedicated server, so this profile got the
  DLL by hand at some point but was never configured.

**How to find the profile that's actually running** (r2modman rewrites
`LogOutput.log` on each modded launch, so the freshest log wins):

```bash
base="$APPDATA/r2modmanPlus-local/Valheim/profiles"
for p in "$base"/*/; do
  echo "$(stat -c '%y' "$p/BepInEx/LogOutput.log" 2>/dev/null)  $(basename "$p")"
done | sort   # newest = the profile you're playing
```

Then check that profile's `BepInEx/config/valcoin_config.json` for placeholder
values, and **restart the game** after fixing it — the plugin reads config once
at startup (`Config.Load()` in `Awake`), so a live session won't pick up an
edit.

> **Gotcha:** a profile name typo (`Heathbound` vs `Hearthbound`) is exactly how
> a profile slips past `deploy.ps1`'s target list. If you play from a profile
> the script doesn't list, every rebuild silently leaves it on an old DLL and
> its config untouched. Add the profile to the script (see below) or rename it
> to match. `deploy.ps1` tolerates missing folders, so a stale/renamed entry
> just prints `SKIP (missing folder)` rather than erroring — easy to miss.

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

## Source of truth

- [backend/app/routes/admin.py](../backend/app/routes/admin.py) — admin reconciliation endpoints
- [valheim-plugin/CoinManager.cs](../valheim-plugin/CoinManager.cs) — local cache + dedupe
- [valheim-plugin/GrantPoller.cs](../valheim-plugin/GrantPoller.cs) — poll loop
