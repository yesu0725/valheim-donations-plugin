# Payment Providers

Each provider's webhook route verifies its own signature and 503s if its
config is missing — partial setups are fine while rolling out one provider
at a time.

## Environment variables

From [backend/.env.example](../backend/.env.example):

### Required
| Variable | Purpose |
|---|---|
| `PLUGIN_TOKEN` | Bearer token the Valheim plugin sends |
| `PUBLIC_BASE_URL` | e.g. `https://your-app.fly.dev` |
| `DONATION_URL` | e.g. `https://your-app.fly.dev/portal` |

### Ko-fi
| Variable | Purpose |
|---|---|
| `KOFI_VERIFICATION_TOKEN` | Shared secret from Ko-fi's webhook settings |
| `KOFI_USERNAME` | For portal deep-links (`ko-fi.com/<username>`) |

### PayPal — **not in use** (removed 2026-08-11)
Auto-credit needs a PayPal **business** account, which this server doesn't have.
All `PAYPAL_*` secrets were unset on Fly, so the portal renders no PayPal card
and `/webhooks/paypal` returns **503**. The handler is left in the codebase,
dormant — setting the variables below is all it takes to bring it back.

| Variable | Purpose |
|---|---|
| `PAYPAL_CLIENT_ID` / `PAYPAL_CLIENT_SECRET` | REST app credentials |
| `PAYPAL_WEBHOOK_ID` | Used to verify webhook signatures |
| `PAYPAL_SANDBOX` | `true` while testing |
| `PAYPAL_BUSINESS_EMAIL` | Receives funds; the portal builds a donate link with the claim code in `custom` so donations auto-credit |
| `PAYPAL_ME_USERNAME` | Optional fallback link (`paypal.me/<username>`) — manual credit only |

### Patreon
| Variable | Purpose |
|---|---|
| `PATREON_WEBHOOK_SECRET` | HMAC-MD5 verification |
| `PATREON_USERNAME` | For portal deep-links |
| `PATREON_CLIENT_ID` / `PATREON_CLIENT_SECRET` | OAuth client (creator portal) |
| `PATREON_REDIRECT_URI` | e.g. `https://your-app.fly.dev/portal/patreon/callback` |

### PayMongo
| Variable | Purpose |
|---|---|
| `PAYMONGO_WEBHOOK_SECRET` | HMAC-SHA256 verification |
| `PAYMONGO_SECRET_KEY` | `sk_test_...` / `sk_live_...` — needed to mint PaymentLinks |

### Optional tuning
| Variable | Default |
|---|---|
| `DB_PATH` | `data/valcoin.sqlite3` |
| `CLAIM_TTL_MINUTES` | `30` |
| `MIN_GRANT_COINS` | `5` |
| `COINS_PER_UNIT` | `{"USD": 50, "PHP": 1, "EUR": 55, "GBP": 65}` |

## Ko-fi
1. Dashboard → **More → API** → set Webhook URL to `/webhooks/kofi`.
2. Copy the verification token → `KOFI_VERIFICATION_TOKEN`.
3. Set `KOFI_USERNAME` so the portal can deep-link to your Ko-fi page.

> **Ko-fi has no message prefill.** Do not add `?message=<code>` to the deep
> link — the param survives in the URL but Ko-fi never reads it into its message
> box, so the donation arrives with no claim code and lands as `unmatched`.
> Donors paste the code themselves, and anyone who forgets can recover it via
> the "Already donated?" form on the portal (`POST /portal/kofi/link`), which
> matches on the donor's Ko-fi email. Anonymous Ko-fi donations carry no email
> and still need `POST /api/admin/credit-unmatched`.

## PayPal — disabled, kept for reference
> **Not offered on this server.** The steps below need a PayPal *business*
> account. Follow them only if you get one; until then leave every `PAYPAL_*`
> variable unset and the provider stays invisible to donors.

1. developer.paypal.com → **Apps & Credentials** → create REST app.
2. **Webhooks** on that app → URL `/webhooks/paypal`, subscribe to
   `PAYMENT.CAPTURE.COMPLETED` (and `PAYMENT.SALE.COMPLETED` if you accept
   classic donations).
3. Copy `Client ID`, `Secret`, and `Webhook ID` into env. Toggle
   `PAYPAL_SANDBOX=true` while testing.
4. Set `PAYPAL_BUSINESS_EMAIL` to the account that receives funds. The portal
   builds a `paypal.com/donate/?business=…&custom=<code>` link — the `custom`
   value comes back as `resource.custom` on the webhook, so donations
   **auto-credit** with no manual step. (`PAYPAL_ME_USERNAME` is only a fallback
   when no business email is set, and requires manual crediting.)

## Patreon
1. Creator portal → **Settings → Webhooks** → add `/webhooks/patreon`.
   Subscribe to `members:pledge:*` and `members:update` / `members:create`.
2. Copy the webhook secret → `PATREON_WEBHOOK_SECRET`.
3. **For OAuth linking** (recommended): also create an API client in the
   developer portal with redirect URI `<your-app>/portal/patreon/callback`.
   Set `PATREON_CLIENT_ID` / `PATREON_CLIENT_SECRET` / `PATREON_REDIRECT_URI`.
4. Set `PATREON_USERNAME` for the portal's Patreon link.
5. First-time patrons: portal's "Link my Patreon account" button does the
   OAuth dance and retroactively credits any pending donations. Renewals
   thereafter auto-credit via `provider_links`.

## PayMongo (GCash + Maya + cards)
1. dashboard.paymongo.com → **Developers → API Keys** → copy the secret key
   into `PAYMONGO_SECRET_KEY`. This is needed to mint PaymentLinks.
2. **Developers → Webhooks** → add `/webhooks/paymongo`, event `payment.paid`.
3. Copy the signing secret → `PAYMONGO_WEBHOOK_SECRET`.
4. The portal mints a PaymentLink with `metadata.claim_code` baked in when
   the donor clicks "Pay" — no manual code entry required from the donor.

## Source of truth

- [backend/.env.example](../backend/.env.example) — full env var template
- [backend/app/config.py](../backend/app/config.py) — settings defaults
- [backend/app/routes/webhooks/](../backend/app/routes/webhooks) — verification logic per provider
