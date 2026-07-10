# Deployment

Backend deploys to Fly.io. The plugin is deployed by copying the built DLL
onto the Valheim dedicated server — see [PLUGIN.md](PLUGIN.md) for that half.

## Live status (2026-07-10)

- **App:** `valheim-donations` → `https://valheim-donations.fly.dev`
- **Region:** `sin`, 1 machine, `min_machines_running = 1` (always awake for webhooks)
- **Volume:** `valcoin_data` (1 GB), created and mounted at `/data`
- **Secrets set:** `PLUGIN_TOKEN`, `PUBLIC_BASE_URL`, `DONATION_URL`,
  `BRAND_*` — plus **all four providers now live:**
  - **Ko-fi:** `KOFI_VERIFICATION_TOKEN`, `KOFI_USERNAME`
  - **Patreon:** `PATREON_WEBHOOK_SECRET`, `PATREON_USERNAME`,
    `PATREON_CLIENT_ID`, `PATREON_CLIENT_SECRET`, `PATREON_REDIRECT_URI`
  - **PayPal:** `PAYPAL_CLIENT_ID`, `PAYPAL_CLIENT_SECRET`, `PAYPAL_WEBHOOK_ID`,
    `PAYPAL_SANDBOX=false`, `PAYPAL_BUSINESS_EMAIL`, `PAYPAL_ME_USERNAME`
  - **PayMongo:** `PAYMONGO_SECRET_KEY` (`sk_live_…`), `PAYMONGO_WEBHOOK_SECRET`
  - Each webhook returns **401** to an unsigned/bad-sig probe (configured &
    verifying) vs **503** when unconfigured — a quick way to confirm a provider
    is wired: `curl -s -o /dev/null -w "%{http_code}" -X POST
    https://valheim-donations.fly.dev/webhooks/<provider>`.
  - **Live money not yet tested** for PayPal or PayMongo (verified with
    synthetic/minted requests only — Ko-fi was end-to-end tested earlier). See
    [PROVIDERS.md](PROVIDERS.md).
- **Plugin side:** `valcoin_config.json` on the dedicated server points
  `backend_url` at the URL above with the matching `plugin_token`.
- Fly requires a payment method on file and may flag new accounts for manual
  "high risk" verification (`fly.io/high-risk-unlock`) before the first
  `flyctl launch` succeeds — both were one-time hurdles during this rollout,
  not expected on every deploy.

## Fly.io configuration

From [backend/fly.toml](../backend/fly.toml):

- **Region:** `sin` (Singapore — closest to PH players)
- **VM:** shared CPU, 256 MB RAM
- **Volume:** 1 GB persistent volume (`valcoin_data`) mounted at `/data`, for
  the SQLite database
- **HTTP service:** `auto_stop_machines = off`, `min_machines_running = 1` —
  webhooks need the app to stay awake to receive provider callbacks

⚠️ The `app` name in `fly.toml` ships as the placeholder
`valheim-donations`. Running `flyctl launch --no-deploy` rewrites it to
whatever name you actually claim — do this before your first deploy.

## First-time deploy

```bash
flyctl auth login
flyctl launch --no-deploy
flyctl volumes create valcoin_data --size 1 --region sin
flyctl secrets set \
  PLUGIN_TOKEN="$(python -c 'import secrets;print(secrets.token_urlsafe(32))')" \
  PUBLIC_BASE_URL="https://<your-app>.fly.dev" \
  DONATION_URL="https://<your-app>.fly.dev/portal" \
  KOFI_VERIFICATION_TOKEN="..." KOFI_USERNAME="yourname"
flyctl deploy
```

## Per-provider secrets

Add these once each provider's account is set up (see [PROVIDERS.md](PROVIDERS.md)
for where each value comes from):

```bash
flyctl secrets set \
  PAYPAL_CLIENT_ID="..." PAYPAL_CLIENT_SECRET="..." PAYPAL_WEBHOOK_ID="..." \
  PAYPAL_SANDBOX="false" PAYPAL_BUSINESS_EMAIL="you@example.com" PAYPAL_ME_USERNAME="..." \
  PATREON_WEBHOOK_SECRET="..." PATREON_USERNAME="..." \
  PATREON_CLIENT_ID="..." PATREON_CLIENT_SECRET="..." \
  PATREON_REDIRECT_URI="https://<your-app>.fly.dev/portal/patreon/callback" \
  PAYMONGO_WEBHOOK_SECRET="..." PAYMONGO_SECRET_KEY="sk_live_..."
```

Notes:

- **PayPal auto-credit** needs `PAYPAL_BUSINESS_EMAIL`. The portal then builds a
  `paypal.com/donate/?business=…&custom=<code>` link; the `custom` value comes
  back as `resource.custom` on the webhook, so donations credit with no manual
  step. `PAYPAL_ME_USERNAME` alone is only a fallback and needs manual crediting.
- **`flyctl secrets set` restarts the machine** with the existing image + new
  env (no code change shipped). Use `--stage` to defer the restart and fold it
  into the next `flyctl deploy` when you're also shipping code.
- Webhook URLs to register in each provider dashboard:
  `…/webhooks/kofi`, `…/webhooks/paypal`, `…/webhooks/patreon`,
  `…/webhooks/paymongo`.

## Redeploy checklist

1. Run backend tests locally first (see [BACKEND.md](BACKEND.md)).
2. `flyctl deploy` from `backend/`.
3. Tail logs (`flyctl logs`) and confirm no startup errors — `init_db()`
   should run cleanly against the existing volume.
4. Smoke-test one webhook end-to-end if the change touched webhook logic
   (see [OPERATIONS.md](OPERATIONS.md) for the verification flow).
5. If the plugin's `plugin_token` or `backend_url` changed, update
   `valcoin_config.json` on the game server and restart it.

## Source of truth

- [backend/fly.toml](../backend/fly.toml) — region, VM size, volume, http_service
- [backend/Dockerfile](../backend/Dockerfile) — container build
