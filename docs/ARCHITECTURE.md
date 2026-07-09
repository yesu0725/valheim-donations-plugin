# Architecture

## Two-part system

```
valheim-donations/
├── backend/         FastAPI service (deploys to Fly.io)
└── valheim-plugin/  BepInEx server-side plugin (+ optional client UI)
```

The backend owns the coin ledger; the plugin owns the perk/SKU effects. They
communicate over a small bearer-token HTTP API — the plugin never touches
the database directly, and the backend never touches game state directly.

## Donation flow

The plugin mints a short-lived **claim code** (`AB12-CD34`, 30-min TTL) when a
player clicks **Donate** in the in-game panel (F4), DMs the donor a portal
URL, and polls `/api/grants/pending` every ~10s. The donor picks a provider
in the portal; each webhook verifies its own signature, resolves the code →
Steam64, and writes a `grants` row. The plugin applies grants, acks them, and
updates a local balance cache.

```
player clicks Donate (F4)
   │  plugin → POST /api/claim
   ▼
backend mints AB12-CD34 (TTL 30 min)
   │  plugin shows the donor: "Donate at /portal, code is AB12-CD34"
   ▼
donor visits /portal/AB12-CD34
   ├── Ko-fi / PayPal → opens hosted page (code prefilled where possible)
   ├── PayMongo       → portal JS calls /portal/paymongo/link → checkout
   └── Patreon        → opens patreon.com + offers "Link my account" (OAuth)
   ▼
provider webhook fires → backend resolves code → grants row
   ▼
plugin polls /api/grants/pending → applies → /api/grants/ack
```

Shop purchases and gifts (Shop/Gift tabs) call atomic `/api/spend` and
`/api/transfer` endpoints, idempotency-keyed so retries are safe. All of
these are in-game panel (F4) actions over a silent RPC — there is no chat or
console command path (see [SHOP.md](SHOP.md#no-chat-or-console-commands)).

The plugin owns the SKU catalog and applies effects locally (cosmetic
`grant_perk` perks today; a proposed `grant_item` effect spawns weekly-limited
consumables). Balance-guarded ecosystem hooks for the sibling mods (BiomeLords,
Lost Scrolls II, ServerGuide, ServerGuard) are documented under
[ecosystem/](ecosystem/donation-hooks.md); the weekly cap for `grant_item` is
enforced on `/api/spend` since the backend owns the ledger.

## Endpoints (quick reference)

Public portal: `/`, `/portal`, `/portal/{code}`, plus PayMongo link mint and
Patreon OAuth start/callback.

Webhooks: `/webhooks/{kofi,paypal,patreon,paymongo}` — each verifies its own
signature.

Plugin/admin (bearer-token): `/api/claim`, `/api/grants/{pending,ack}`,
`/api/grants/balance/{steam64}`, `/api/spend`, `/api/transfer`,
`/api/spends/{steam64}`, `/api/state/{steam64}`, `/api/leaderboard/top`,
plus admin tools under `/api/admin/*`.

Full table with methods and verification details is in
[BACKEND.md](BACKEND.md) and [PROVIDERS.md](PROVIDERS.md).

## Database schema

SQLite (WAL mode). Tables, from [backend/app/schema.sql](../backend/app/schema.sql):

| Table | Purpose |
|---|---|
| `players` | Steam64 → running total_coins, last seen name |
| `claim_codes` | Short-lived codes minted by `/donate`, TTL-expired |
| `donations` | Every webhook receipt, `UNIQUE(provider, provider_txn_id)` for idempotency |
| `oauth_states` | Patreon OAuth CSRF state, 10-min TTL |
| `grants` | Coin credits pending/delivered/acked to the plugin |
| `provider_links` | provider_user_id ↔ steam64 bindings (e.g. linked Patreon accounts) |
| `spends` | Append-only purchase/transfer ledger, `idempotency_key` UNIQUE |

## Idempotency model

- **Webhooks** are idempotent via `donations(provider, provider_txn_id)` UNIQUE —
  provider retries can't double-credit.
- **Grant delivery** is doubly idempotent: the backend tracks ack state per
  grant, and the plugin separately caches the last 5000 applied grant ids
  locally, so a crash-then-replay on either side is safe.
- **Spends/transfers** are idempotency-keyed by the plugin, so a flaky network
  retry on `/api/spend` or `/api/transfer` can't double-debit.

## Source of truth

- [backend/app/main.py](../backend/app/main.py) — router wiring
- [backend/app/schema.sql](../backend/app/schema.sql) — schema
- [valheim-plugin/GrantPoller.cs](../valheim-plugin/GrantPoller.cs) — polling loop
- [valheim-plugin/CoinManager.cs](../valheim-plugin/CoinManager.cs) — local dedupe cache
