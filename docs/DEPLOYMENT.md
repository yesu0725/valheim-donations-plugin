# Deployment

Backend deploys to Fly.io. The plugin is deployed by copying the built DLL
onto the Valheim dedicated server — see [PLUGIN.md](PLUGIN.md) for that half.

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
  PAYPAL_CLIENT_ID="..." PAYPAL_CLIENT_SECRET="..." PAYPAL_WEBHOOK_ID="..." PAYPAL_ME_USERNAME="..." \
  PATREON_WEBHOOK_SECRET="..." PATREON_USERNAME="..." \
  PATREON_CLIENT_ID="..." PATREON_CLIENT_SECRET="..." \
  PATREON_REDIRECT_URI="https://<your-app>.fly.dev/portal/patreon/callback" \
  PAYMONGO_WEBHOOK_SECRET="..." PAYMONGO_SECRET_KEY="sk_live_..."
```

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
