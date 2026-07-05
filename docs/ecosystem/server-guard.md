# Valheim-ServerGuard — Knowledge Notes

**Repo folder:** `E:\Valheim Modding\Valheim-ServerGuard`  (v1.4.0)
GUID `com.taeguk.valheim.serverguard.client`. Server DLL + client companion DLL.

## Concept

Locks a dedicated server to a specific modpack. Every player runs a small
companion plugin that reports its loaded mod list, HMAC-signed with a shared
password. The server compares against an allowlist and kicks vanilla /
wrong-modpack / unsigned clients.

## How protection works

1. Player connects → server sends a random challenge.
2. Companion replies: mod list + challenge + timestamp + HMAC (shared secret).
3. Server verifies signature, challenge freshness, clock skew, and that every
   mod is allowed / no banned mods / all required mods present.
4. Fail → kicked with a clear reason. No companion within 10s → kicked.
5. `violationThreshold` (default 3) rejections → auto-ban.

## Config (server, live-reloaded)

`settings.yaml` (secret + switches), `admins.yaml` (bypass Steam IDs),
`allowed_mods.yaml` (`required_mods` / `allowed_mods` / `banned_mods`,
matched by GUID or `GUID|hash`). Client side: just `client.yaml` with the secret.

Notable options: `enforce`, `requireCompanion`, `requireHmac`, `allowUnlisted`,
`characterLimit`, `discordWebhookUrl` (kick/ban/violation alerts).

## Donation-relevant hooks (indirect but real)

- **It is the guarantor that everyone on the server is running the modpack** —
  which means the donation plugin, ServerGuide, BiomeLords, and Lost Scrolls II
  are *guaranteed present on every client*. Donation promos authored in
  ServerGuide will reach 100% of players; no "vanilla client can't see it" gap.
- The donation plugin can safely be a **required_mod**, so `/donate`, the F8
  panel, and the welcome HUD line are universally available.
- ServerGuard's own **Discord webhook** already funnels server events to Discord
  — the same channel the donation Discord webhook would post to (unified
  community feed).
- **Not** a place to gate content behind donations — its whole job is fairness /
  anti-cheat, so keep it strictly about modpack integrity, never pay-to-join.
