# Valheim Donations

Lets Valheim players donate via Ko-fi, Patreon, or PayMongo and get **Valcoins** credited in-game automatically, redeemable in a configurable in-game shop for perks and weekly-limited consumables. **Requires the plugin installed on both server and client** — donations happen entirely through an in-game panel, not chat commands.

## What it does

- **One in-game panel, opened with F4** — a browsable, offline-resilient home for donating, the shop, gifting, and the patron leaderboard, that lights up automatically once the server's donation backend comes online. All actions (donate, buy, gift, admin give/remove) happen here, over a silent RPC — nothing is typed into public chat.
- **Simple donate flow** — click one button to get your code, copy it, and open the donation portal in your browser; your Valcoins credit automatically. The tab leads with a clear **$1 USD = N Valcoins** rate (served by your backend, so it always matches what donations actually credit).
- **Any of three providers** (Ko-fi, Patreon, PayMongo) can be wired up independently; each webhook verifies its own signature and credits Valcoins automatically within seconds.
- **A configurable shop** (`valcoin_shop.yaml`) sells:
  - **Soulkeeper Charm** (`add_charges`) — death insurance: on death you keep your skills (no drain) and a Valkyrie carries you back to your tombstone, scattering nearby creatures on arrival. Capped at 10 charges per player per week.
  - **Familiars** (`armor_vfx`) — a miniature flying creature (Bat, Ghost, Deathsquito, Drake, Wraith, Volture, Gjall, Fallen Valkyrie) hovers at your shoulder, bound to your equipped helmet. Grants feather fall and a small flat attack bonus. Other players see it too.
  - **Weekly-capped, boss-gated consumables** (`grant_item`) — feasts, meads, and bulk materials spawned directly into the buyer's inventory server-side.
- **Preview images** — give any shop item a picture via `preview_image` (an `https` URL or a file in `BepInEx/config`). It shows as a thumbnail in the shop row, larger in the purchase-confirm dialog, and clicking either opens a full-size view.
- **Earn Valcoins by playing** (optional, needs [ValheimServerGuide](https://thunderstore.io/c/valheim/p/TaegukGaming/ValheimServerGuide/)) — a one-time welcome quest worth 30 Valcoins that introduces the donation system, plus daily quests (hunt, tame, fell a Biome Lord, bond with a Dvergr companion) capped at 8 per day with a 7-day streak bonus. Quest earnings never appear on the Top Patrons board, and the cap keeps them a taster rather than a substitute for donating.
- **Gift Valcoins** to other players from the panel; a built-in **Top Patrons** leaderboard shows lifetime donors.
- **Admin tab** (admin-only) to manually give/remove a player's balance.

## Quick setup

1. Install this mod on your **dedicated server AND every player's client** — the client copy is required, not optional; there's no chat-command fallback for un-modded players.
2. Deploy the companion FastAPI backend (see the [GitHub repo](https://github.com/yesu0725/valheim-donations-plugin) — deploys to Fly.io in a few commands).
3. Launch the server once. It writes a template `BepInEx/config/valcoin_config.json` — fill in `backend_url` and `plugin_token` from your backend deploy, then restart.
4. Edit `BepInEx/config/valcoin_shop.yaml` to set your shop catalog (also auto-templated on first run).
5. List admin Steam64 IDs in `BepInEx/config/valcoin_admins.yaml` to unlock the panel's Admin tab for them.
6. *(Optional — quests.)* If you run ValheimServerGuide, copy `guidance.valcoin-quests.yaml` from the repo into the **server's** `BepInEx/config/ValheimServerGuide/`. Payouts are priced in `BepInEx/config/valcoin_quests.yaml`, auto-templated on first run. Needs backend 0.7.0+.

That's the minimum. Everything else — provider webhooks, weekly-capped consumables — is documented in the repo.

## Documentation

Full setup, provider configuration, and shop schema are in the [GitHub repo docs](https://github.com/yesu0725/valheim-donations-plugin/tree/main/docs).

## Try it out

This mod was built for the **TaegukGaming community server** running the **Hearthbound modpack**.

🏰 **[Hearthbound Valheim Modpack](https://thunderstore.io/c/valheim/p/TaegukGaming/Hearthbound_Valheim_Modpack/)**

## Disclaimer

This mod is **created using AI**. No other mods were copied during the process. All feature ideas come from the uploader and are mainly to cater the needs of the **TaegukGaming community server**. If any features or ideas look similar to other mods, these are not intentional.

This mod is **free to use as is**. Voluntary support is appreciated.

---

**Version:** 5.22.2
**Source / issues:** https://github.com/yesu0725/valheim-donations-plugin
**Backend (required, self-hosted):** https://github.com/yesu0725/valheim-donations-backend
