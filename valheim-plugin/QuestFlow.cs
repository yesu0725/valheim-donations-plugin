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

    public static void Run(long senderPeerID, string steam64, string playerName, string questId, Action<string> reply)
    {
        if (string.IsNullOrEmpty(steam64))
        {
            Debug.LogWarning($"[Valcoin] quest '{questId}' reported by a peer with no resolvable Steam ID; ignoring.");
            return;
        }
        // No ack in either of the next two cases, deliberately. The client keeps
        // its key and retries, so a backend that isn't configured yet — or a
        // quest whose price hasn't been added — postpones the payout instead of
        // burning it. The warning repeating each retry is the intended signal to
        // the operator that something is misconfigured.
        if (!Config.Ready)
        {
            Debug.LogWarning($"[Valcoin] quest '{questId}' reported by {playerName} but the backend "
                             + "isn't configured; not acknowledging (client will retry).");
            return;
        }

        var quest = QuestCatalog.Get(questId);
        if (quest == null)
        {
            Debug.LogWarning($"[Valcoin] Unknown quest '{questId}' reported by {playerName} — no price in "
                             + "valcoin_quests.yaml. Not acknowledging (client will retry).");
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
                // Event prizes opt out of the daily allowance (backend 0.10.0+).
                // An older backend ignores the field and caps as before.
                capped   = quest.Capped,
            },
            (ok, r, err) =>
            {
                if (!ok || r == null)
                {
                    // No ack — the client keeps its key and retries, so a
                    // backend blip delays the payout rather than eating it.
                    Debug.LogWarning($"[Valcoin] quest claim failed for {playerName} " +
                                     $"({quest.Id}): {err ?? "unknown"} — not acknowledging (client will retry).");
                    return;
                }

                // Definitive answer (credited / already claimed / capped): the
                // key has done its job, so release the client to clear it.
                RpcLayer.SendQuestAck(senderPeerID, quest.Id);
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
                //
                // But say the RIGHT thing: a `period: once` quest has no reset, and
                // the old text said "already claimed today ... resets in 7h 16m" for
                // every quest alike, inventing a cooldown that does not exist. The
                // reset field the backend returns is the DAILY one; it is meaningless
                // for a one-time quest, and printing it made a permanently-claimed
                // quest look like a broken timer.
                if (quest.Period == "once")
                {
                    reply($"{quest.Name} — already claimed. This is a one-time quest; "
                          + "it doesn't come back.");
                    // Worth a server-side line: a one-time quest being re-reported is
                    // normally harmless, but it is also what an operator sees when a
                    // payout was raised AFTER players had already banked the old
                    // amount. They cannot claim the difference — the id is spent —
                    // so the operator has to settle it by hand if they want to.
                    Debug.Log($"[Valcoin] one-time quest '{quest.Id}' re-reported by {playerName} "
                              + $"(already claimed; current price {quest.Coins}). Nothing paid.");
                }
                else
                {
                    reply($"{quest.Name} — already claimed today. Resets in {r.resets_in}.");
                }
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
