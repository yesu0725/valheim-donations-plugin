using System;
using System.Collections.Generic;
using UnityEngine;

// Server-side handler for a quest completion reported by QuestWatcher.
//
// The client sends only a quest id. Everything that decides value happens here
// and in the backend: this looks the payout up in the server's own
// valcoin_quests.yaml, and the backend decides whether that payout is actually
// owed (already claimed today? daily cap reached?).
//
// Coin delivery deliberately rides the existing grant pipeline rather than
// messaging the player directly: the backend writes a `grants` row, GrantPoller
// picks it up on its next tick and shows the "+N Valcoins!" toast, and
// CoinManager's local balance cache stays correct. This handler only says the
// things GrantPoller can't know — which quest it was, and where the player
// stands against the daily cap.
public static class QuestFlow
{
    private class ClaimResp
    {
        public string status;          // credited | already_claimed | cap_reached
        public int    coins_awarded;
        public bool   capped;
        public int    daily_earned;
        public int    daily_cap;
        public string resets_in;
        public int    streak;
        public int    streak_bonus;
        public int    balance;
    }

    // steam64 -> UTC date on which we last toasted "cap reached" to them.
    // A player at the cap keeps finishing quests, and each one comes back
    // cap_reached; without this they'd get the same toast every time. The panel
    // log still records every occurrence, so nothing is hidden — it just stops
    // shouting. Keyed by date rather than session so a relog doesn't re-nag.
    private static readonly Dictionary<string, string> _capToastDay = new Dictionary<string, string>();

    public static void Run(string steam64, string playerName, string questId, Action<string> reply)
    {
        if (string.IsNullOrEmpty(steam64))
        {
            Debug.LogWarning($"[Valcoin] quest '{questId}' reported by a peer with no resolvable Steam ID; ignoring.");
            return;
        }
        if (!Config.Ready) return;

        var quest = QuestCatalog.Get(questId);
        if (quest == null)
        {
            // Either a stale ServerGuide YAML naming a quest this server doesn't
            // price, or a hand-crafted RPC. Both are a no-op — an unpriced quest
            // can't pay anything.
            Debug.LogWarning($"[Valcoin] Unknown quest '{questId}' reported by {playerName}; ignoring.");
            return;
        }

        SharedCoroutineRunner.Instance.StartCoroutine(BackendClient.Post<ClaimResp>(
            "/api/quests/claim",
            new
            {
                steam64,
                quest_id = quest.Id,
                coins    = quest.Coins,
                period   = quest.Period,
                name     = playerName,
            },
            (ok, r, err) =>
            {
                if (!ok || r == null)
                {
                    // Not surfaced to the player: they completed the quest, the
                    // key is already cleared, and a backend blip isn't something
                    // they can act on. It is a real (if rare) lost payout, so it
                    // is logged loudly enough to reconcile from.
                    Debug.LogWarning($"[Valcoin] quest claim failed for {playerName} " +
                                     $"({quest.Id}): {err ?? "unknown"}");
                    return;
                }
                Announce(steam64, playerName, quest, r, reply);
            }));
    }

    private static void Announce(
        string steam64, string playerName, QuestCatalog.Quest quest,
        ClaimResp r, Action<string> reply)
    {
        var progress = $"{r.daily_earned}/{r.daily_cap} today";

        switch (r.status)
        {
            case "credited":
                // The coin toast is GrantPoller's job; this is the "what for".
                reply($"{quest.Name} — +{r.coins_awarded} Valcoins ({progress})");
                if (r.capped)
                    Toast(steam64, $"<color=yellow>Daily cap reached ({r.daily_earned}/{r.daily_cap})</color> — resets in {r.resets_in}");
                if (r.streak_bonus > 0)
                {
                    reply($"{r.streak}-day streak — +{r.streak_bonus} Valcoins");
                    Toast(steam64, $"<color=yellow>{r.streak}-day streak!</color> +{r.streak_bonus} Valcoins");
                }
                Debug.Log($"[Valcoin] quest '{quest.Id}' paid {r.coins_awarded} to {playerName} ({progress}).");
                break;

            case "already_claimed":
                // Quiet on purpose. Re-completing a daily is normal play, not a
                // mistake worth interrupting anyone over.
                reply($"{quest.Name} — already claimed today. Resets in {r.resets_in}.");
                break;

            case "cap_reached":
                reply($"{quest.Name} — daily cap reached ({r.daily_earned}/{r.daily_cap}). Resets in {r.resets_in}.");
                if (ShouldToastCap(steam64))
                    Toast(steam64, $"<color=yellow>Daily Valcoin cap reached ({r.daily_earned}/{r.daily_cap})</color> — resets in {r.resets_in}");
                break;
        }
    }

    private static bool ShouldToastCap(string steam64)
    {
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        if (_capToastDay.TryGetValue(steam64, out var last) && last == today) return false;
        _capToastDay[steam64] = today;
        return true;
    }

    private static void Toast(string steam64, string message)
    {
        try
        {
            SteamIdResolver.OnlinePlayerFor(steam64)
                ?.Message(MessageHud.MessageType.TopLeft, message);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Valcoin] quest toast failed: {ex.Message}");
        }
    }
}
