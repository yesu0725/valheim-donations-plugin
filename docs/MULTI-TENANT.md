# Multi-Tenant Platform — design notes

**Status: not built. Exploratory spec, written 2026-08-12.** Nothing here is
committed to. It exists so the idea can be picked up later without re-deriving
the reasoning, and so the decisions that must be made *before* writing code are
visible up front.

## The idea

Turn the donation system into something **other Valheim server operators** can
run for their own communities. Each operator gets their own tenant: their own
donors, ledger, shop, branding and provider accounts. They monetise the
community on the server they control; we supply software.

### Why this shape and not the obvious one

The tempting version — ship the shop inside individual mods so anyone using the
mods can buy items — fails on two structural problems, and they are worth
restating here because they are what this design exists to avoid:

1. **Shipping the plugin to untrusted machines means shipping a credential.**
   Today one `plugin_token` unlocks `/api/admin/grant`, `/api/spend`,
   `/api/claim` and the ledger. A secret distributed to players is not a secret.
2. **We can't deliver what we'd be selling.** The server applies the item. On a
   world we don't run, the client is the authority, and a buyer can spawn the
   same item from the console in seconds.

Multi-tenancy keeps the trust boundary where it already works: **a server the
operator controls, authenticated by a credential that never leaves it.**

## Money never touches us

**Each operator connects their own Ko-fi / Patreon / PayMongo accounts.** Funds
go operator → their provider → their bank. We never hold, route or remit
donor money.

This is the single most important constraint in the design. The alternative —
collecting donations and paying operators out — makes us a payment
intermediary, which in most jurisdictions means money-transmitter licensing,
holding other people's funds, and merchant-of-record liability for every
community on the platform. That is a different company, not a feature.

Revenue, if any, is a **flat B2B subscription from operator to us**, billed
separately (Stripe/Paddle) and completely decoupled from donation flow. We sell
software to server owners; they receive donations from their players. Neither
transaction is "selling game items to end users," which is the framing that
attracts trouble from Iron Gate, Thunderstore and payment providers alike.

## Data model

Every table becomes tenant-scoped. The work is mechanical but touches
everything.

```
tenants(id, slug, name, status, created_at)
```

Add `tenant_id` to: `players`, `claim_codes`, `donations`, `grants`, `spends`,
`provider_links`, `charges`, `charge_grants`, `quest_claims`.

Uniqueness constraints must be re-scoped, or one tenant's data collides with
another's:

| Today | Becomes |
|---|---|
| `UNIQUE(provider, provider_txn_id)` on donations | `UNIQUE(tenant_id, provider, provider_txn_id)` |
| `UNIQUE(idempotency_key)` on spends | `UNIQUE(tenant_id, idempotency_key)` |
| `PRIMARY KEY(provider, provider_user_id)` on provider_links | add `tenant_id` |
| `UNIQUE(steam64, quest_id, period_key)` on quest_claims | add `tenant_id` |

**A player's balance is per-tenant.** Someone who plays on two servers has two
balances and appears on two leaderboards. That is correct — the coins were
donated to a specific community — but it should be stated plainly to donors so
nobody expects coins to travel.

**Claim codes stay globally unique.** They're 8 characters from a 31-character
alphabet (~8.5×10¹¹ combinations), so collisions remain negligible, and keeping
them global means a donor types their code into one portal and the backend
resolves which tenant it belongs to. No "pick your server" step — worth
preserving, since every extra step on a donation page costs conversions.

## Auth

The current single `plugin_token` splits into per-tenant, per-purpose
credentials:

- **`server_token`** — held by the operator's game server. Scoped to that
  tenant's rows only. Never grants admin.
- **`admin_token`** — for the operator's ledger and manual credits. Separate
  secret, separate blast radius.
- Both rotatable without redeploying the plugin.

**The plugin needs no concept of tenants.** It sends its token; the backend
resolves tenant from the token. That's worth designing for deliberately: it
means the existing plugin keeps working unchanged, and the tenancy is invisible
to the thing running on someone else's machine.

> Split admin off the plugin token **now**, before any of this. Today a game
> server holds a credential that can mint unlimited coins, which is more blast
> radius than it needs regardless of whether this platform is ever built.

### Webhooks must carry the tenant in the URL

Provider payloads have no field we can use to identify the tenant, so the
**URL** must: `/webhooks/kofi/{tenant_slug}`, and each tenant supplies their own
verification token, verified against that tenant's secret. Same for Patreon and
PayMongo. Getting this wrong means one operator's donations credit another's
players.

## Portal

Because claim codes are globally unique, `/portal/{code}` can look up the
tenant and render **that operator's** branding, providers and copy — the donor
never picks a server.

Per-tenant hostnames (`{slug}.donate.example.com`) would need a wildcard
certificate and add operational weight. Path-based (`/t/{slug}/…`) for the entry
page is simpler and should be the default until someone asks otherwise.

## Onboarding an operator

1. Create tenant → slug, `server_token`, `admin_token`.
2. Operator pastes **their own** provider credentials; we generate their webhook
   URLs to register at each provider.
3. Operator sets branding (name, logo, Discord, contact, coin rate).
4. Install the plugin; put `backend_url` + `server_token` in
   `valcoin_config.json`.
5. Configure `valcoin_shop.yaml`.
6. Test donation end-to-end.

Step 2 is the one that will generate support tickets, and it's the same class of
problem that produced the Ko-fi prefill bug — **an integration assumption nobody
tested against the real provider**. Onboarding should end with a *verified* test
donation, not a checklist the operator ticks.

## Migration from single-tenant

Straightforward, because there is exactly one tenant today:

1. Create `tenants`; insert the existing deployment as tenant 1.
2. Add `tenant_id` columns defaulting to 1; backfill; then enforce `NOT NULL`.
3. Re-scope unique constraints (SQLite: table rebuild, not `ALTER`).
4. Map the existing `PLUGIN_TOKEN` to tenant 1's `server_token` so the running
   server keeps working through the cutover.

## Infrastructure

SQLite on a single Fly volume is fine for one tenant and probably fine for the
first several. Two things force a rethink:

- **Concurrent writes.** SQLite serialises them; many busy servers polling
  `/api/grants/pending` every 10s will contend.
- **Backup/restore granularity.** One operator asking to restore their data
  means restoring everyone's.

Postgres when either bites — not before. Note it as a known cliff rather than
pre-building for scale that may never arrive.

## Risks that do *not* go away

- **We become the shared host.** Many operators' donation pages on one domain is
  structurally the same problem as `*.fly.dev`: one bad actor's page gets the
  whole domain classified as phishing, and every tenant goes down with it. This
  session was spent recovering from exactly that. Mitigations: per-tenant
  hostnames, review before a tenant goes live, fast takedown.
- **We hold other people's donor data.** Emails, Patreon member ids, payment
  metadata. That makes us a data processor with GDPR/DPA obligations —
  processing agreements, deletion on request, breach notification.
- **Operator conduct reflects on the platform.** Someone running a
  blatantly pay-to-win shop, or a fake charity, becomes our reputational
  problem. Needs operator terms and the ability to suspend a tenant.
- **Iron Gate / Coffee Stain.** Enabling monetisation of their game at scale
  raises our profile even though we sell only software. Materially better than
  selling items directly, not zero.
- **Support burden across servers we don't control**, with mod conflicts we
  can't reproduce.

## Decisions needed before any code

1. Free, or paid subscription? Changes whether operator billing exists at all.
2. Open signup, or invite-only while the abuse story is unsolved?
3. Per-tenant hostnames from day one, or path-based until it hurts?
4. Do we publish operator terms, and who enforces them?
5. Is quest/ServerGuide integration in scope per tenant, or core-only first?

## Rough phasing

| Phase | Work |
|---|---|
| 0 | Split admin token off `plugin_token` *(do regardless)* |
| 1 | `tenants` table, `tenant_id` everywhere, re-scoped constraints, migration |
| 2 | Per-tenant tokens + resolution from token |
| 3 | Per-tenant webhook URLs and provider credentials |
| 4 | Tenant-aware portal + branding |
| 5 | Operator self-service: signup, token rotation, provider setup, test donation |
| 6 | Operator ledger scoped to tenant |
| 7 | Terms, suspension, data deletion |

Phases 1–3 are where the correctness risk lives: every one of those constraint
changes is a way for one tenant's money to land in another's ledger. That part
deserves the same treatment the donation paths got — an end-to-end harness that
drives two tenants at once and asserts nothing crosses between them.

## See also

- [ARCHITECTURE.md](ARCHITECTURE.md) — the single-tenant design this extends
- [OPERATIONS.md](OPERATIONS.md) — idempotency and reconciliation, all of which
  becomes per-tenant
- [ecosystem/](ecosystem/README.md) — the sibling mods and why in-mod shops were
  rejected in favour of this
