# Valheim Donations — Project Guide

Two-part system that lets Valheim players (vanilla or modded) donate via Ko-fi /
PayPal / Patreon / PayMongo and get **Valcoins** credited in-game, redeemable in
an in-game shop for perks.

## Layout

```
valheim-donations/
├── backend/         FastAPI service (deploys to Fly.io)
├── valheim-plugin/  BepInEx server-side plugin (+ optional client UI)
├── scripts/         Repo tooling (setup-guide PDF generator)
└── docs/            Detailed docs — see index below
```

## Architecture in one paragraph

The plugin mints a short-lived **claim code** (`AB12-CD34`, 30-min TTL) when a
player runs `/donate`, DMs the donor a portal URL, and polls
`/api/grants/pending` every ~10s. The donor picks a provider in the portal;
each webhook verifies its own signature, resolves the code → Steam64, and
writes a `grants` row. The plugin applies grants, acks them, and updates a
local balance cache. Shop purchases (`/buy`) and gifts (`/gift`) call atomic
`/api/spend` and `/api/transfer` endpoints, idempotency-keyed so retries are
safe. The backend owns the coin ledger; the plugin owns the perk/SKU effects.

## Documentation index

| Doc | Covers |
|---|---|
| [docs/STATUS.md](docs/STATUS.md) | Current versions, phase, known build/config discrepancies |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | Donation flow, endpoint map, DB schema, idempotency model |
| [docs/BACKEND.md](docs/BACKEND.md) | FastAPI layout, endpoints, currency conversion, local dev, tests |
| [docs/PLUGIN.md](docs/PLUGIN.md) | BepInEx plugin layout, build, required DLLs, config files |
| [docs/PROVIDERS.md](docs/PROVIDERS.md) | Ko-fi / PayPal / Patreon / PayMongo setup + env vars |
| [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) | Fly.io launch, secrets, volume, redeploy checklist |
| [docs/SHOP.md](docs/SHOP.md) | Chat commands, shop YAML schema, `grant_item` weekly-limited consumables, F8 panel + F4 Donation Codex, advertising kit |
| [docs/ecosystem/](docs/ecosystem/README.md) | Sibling mods (BiomeLords, Lost Scrolls II, ServerGuide, ServerGuard) + balance-guarded donation-promotion proposals |
| [docs/OPERATIONS.md](docs/OPERATIONS.md) | Idempotency safety nets, reconciliation, common errors |
| [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) | Local setup for both halves, keeping the setup-guide PDF current |
| [docs/ROADMAP.md](docs/ROADMAP.md) | Planned but unimplemented perks (armor customization, overcharge, reinforce) |

Each backend/plugin also has its own detailed README
([backend/README.md](backend/README.md),
[valheim-plugin/README.md](valheim-plugin/README.md)) that the docs above
summarize and link back to.

## Quick start

```powershell
# Backend
cd backend
python -m venv .venv ; .\.venv\Scripts\Activate.ps1
pip install -r requirements-dev.txt
copy .env.example .env
uvicorn app.main:app --reload --port 8080

# Plugin
cd ..\valheim-plugin
dotnet build -c Release
```

Full instructions: [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).
