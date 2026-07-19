# Backend

FastAPI service that receives donation webhooks, credits Valcoins, and serves
the polling API the plugin reads from. Includes the public donor-facing
portal.

## Layout

- [app/main.py](../backend/app/main.py) — FastAPI app + router wiring
- [app/schema.sql](../backend/app/schema.sql) — SQLite schema
- [app/config.py](../backend/app/config.py) — env-driven settings (pydantic)
- [app/routes/](../backend/app/routes) — endpoint groups: `claim`, `grants`,
  `spend`, `admin`, `leaderboard`, `state`, `portal`
- [app/routes/webhooks/](../backend/app/routes/webhooks) — one file per
  provider: `kofi.py`, `paypal.py`, `patreon.py`, `paymongo.py`
- [tests/](../backend/tests) — pytest suite (11 test files)
- [Dockerfile](../backend/Dockerfile) — container build for Fly.io

## Endpoints

### Public
| Method | Path | Purpose |
|--------|------|---------|
| GET | `/` | Health check |
| GET | `/portal` | Donor portal — enter a claim code |
| GET | `/portal/{code}` | Provider chooser (Ko-fi / PayPal / Patreon / PayMongo) |
| POST | `/portal/paymongo/link` | Mint a PayMongo PaymentLink (called by portal JS) |
| GET | `/portal/patreon/start` | Start Patreon OAuth linking |
| GET | `/portal/patreon/callback` | Finish Patreon OAuth → bind account |

### Webhooks (each verifies its own signature — see [PROVIDERS.md](PROVIDERS.md))
| Method | Path | Verification |
|--------|------|---------------|
| POST | `/webhooks/kofi` | shared verification token |
| POST | `/webhooks/paypal` | PayPal verify-signature API |
| POST | `/webhooks/patreon` | HMAC-MD5 of raw body |
| POST | `/webhooks/paymongo` | HMAC-SHA256 of `t.body` |

### Plugin / admin (bearer-token auth)
| Method | Path | Purpose |
|--------|------|---------|
| POST | `/api/claim` | Mint a claim code for a Steam64 / PlayFab id |
| GET | `/api/grants/pending` | Plugin pulls undelivered grants |
| POST | `/api/grants/ack` | Plugin confirms grants applied in-game |
| GET | `/api/grants/balance/{steam64}` | Lookup a player's running balance |
| POST | `/api/spend` | Atomic, idempotent debit for shop purchases. Enforces the per-player weekly cap for `grant_item` consumables (429 past the cap), and — for `add_charges` SKUs — credits `grant_charges` of `charge_kind` to the `charges` table in the same transaction, subject to an optional `weekly_charge_cap` (shared across every SKU of that `charge_kind`, counted from `charge_grants`; 429 with no debit if it would be exceeded) |
| POST | `/api/charges/consume` | Plugin reports a charge was used (e.g. a Soulkeeper charge on death). Atomic decrement; returns `{consumed, remaining}` (`consumed:false, remaining:0` when the pool is empty) |
| POST | `/api/transfer` | Player-to-player gift (debit + grant) |
| GET | `/api/spends/{steam64}` | Purchase history for one player (audit/debug) |
| GET | `/api/leaderboard/top` | Lifetime donor ranking |
| GET | `/api/state/{steam64}` | One-shot snapshot: balance + leaderboard + totals, plus `owned_skus`, `weekly_usage`, `week_resets_in`, and `charges` (per-kind consumable counts) so the client can render the shop's owned/weekly/charge states. Also returns `coins_per_usd` (from `settings.coins_per_unit["USD"]`) so the in-game panel shows an authoritative exchange rate rather than a hard-coded one |
| POST | `/api/admin/links` | Bind provider account ↔ Steam64 manually |
| POST | `/api/admin/credit-unmatched` | Retroactively credit an `unmatched` donation |
| POST | `/api/admin/grant` | Free-form coin adjustment |
| GET | `/api/admin/unmatched` | List donations awaiting manual reconciliation |

## Currency conversion

`COINS_PER_UNIT` (JSON env var) controls the rate. Defaults from
[app/config.py](../backend/app/config.py): `$1 = 50 coins`, `₱1 = 1 coin`,
`€1 = 55`, `£1 = 65`. Donations below `MIN_GRANT_COINS` (default 5) are
recorded as `rejected` for audit but not credited.

## Local dev

```powershell
cd backend
python -m venv .venv ; .\.venv\Scripts\Activate.ps1
pip install -r requirements-dev.txt
copy .env.example .env   # fill in tokens
uvicorn app.main:app --reload --port 8080
```

## Running tests

```powershell
.\.venv\Scripts\python.exe -m pytest
```

`pytest` is not installed in the base Python env — it only exists inside the
venv created above. README claims 39 tests passing across the 11 test files
in [tests/](../backend/tests).

## Source of truth

- [backend/app/main.py](../backend/app/main.py) — version + router list
- [backend/app/config.py](../backend/app/config.py) — settings defaults
- [backend/README.md](../backend/README.md) — original detailed reference
