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

It only answers `/coins` instantly without a network round-trip. The
backend's SQLite is always the source of truth. If the two ever disagree
(e.g. after a manual `/api/admin/grant`), the plugin's next poll cycle
reconciles it.

## Common errors

| Symptom | Likely cause |
|---|---|
| 503 from a `/webhooks/*` route | That provider's env vars are unset on the backend. Set them and redeploy — see [PROVIDERS.md](PROVIDERS.md). |
| Plugin logs `Backend ready: False` | `backend_url` or `plugin_token` in `valcoin_config.json` is wrong, or the backend isn't reachable from the server box. |
| Plugin builds but won't load | A Unity DLL is missing from `libs/` — see [PLUGIN.md](PLUGIN.md)'s DLL table. |
| Donor paid but no in-game credit | Check the unmatched list first. If the donation isn't there at all, the webhook didn't fire — most provider dashboards have a manual retry/redeliver button. |
| `/buy` on a consumable says "cap reached" | Expected once a `grant_item` SKU's **weekly cap** is hit for that player — resets on the weekly boundary (recommend Monday 00:00 server time). Enforced backend-side on `/api/spend`. |
| `/buy` on a food/mead SKU is refused | The SKU's `requires_boss` gate isn't satisfied — the player/world hasn't defeated the gating boss yet. |
| Coins debited but consumable not received | A `grant_item` SKU with a **wrong prefab id** (e.g. an unverified Ashlands food) charges and gives nothing. Verify prefab ids against your Valheim version. |
| *(removed)* `/sethome` / `/home` / `/shout` | These commands + their perks were removed by design decision — see [SHOP.md](../docs/SHOP.md). |

## Verifying a change end-to-end

1. Run `/donate` in-game — plugin should DM a portal URL with a fresh code.
2. Open the URL — portal should show the code and four provider buttons.
3. Make a test donation (Ko-fi or PayPal sandbox is easiest).
4. Watch backend logs for the webhook hit and a new `grants` row.
5. Within `poll_interval_seconds` (default 10s), the plugin should pop a HUD
   message and `/coins` should reflect the new balance.
6. Exercise `/buy` and `/gift` to confirm `/api/spend` and `/api/transfer`
   are behaving.

## Source of truth

- [backend/app/routes/admin.py](../backend/app/routes/admin.py) — admin reconciliation endpoints
- [valheim-plugin/CoinManager.cs](../valheim-plugin/CoinManager.cs) — local cache + dedupe
- [valheim-plugin/GrantPoller.cs](../valheim-plugin/GrantPoller.cs) — poll loop
