using System;
using System.Linq;
using UnityEngine;

// Server-side Valcoin wallet API for SIBLING MODS in the ecosystem.
//
// Why this exists
// ---------------
// Until now the only way another mod could touch Valcoins was to CREDIT them,
// and only indirectly: a ServerGuide quest sets a `VC.Q.<id>` player key,
// QuestWatcher reports it, and QuestFlow prices it from this server's own
// valcoin_quests.yaml. That is deliberate — the sibling mod never names an
// amount, so it can never inflate a payout.
//
// There was no DEBIT path at all, which blocked wagering features (Lost Scrolls
// II's tournament entry fees and duel stakes). A sibling mod cannot debit on its
// own: the ledger is backend-authoritative, and a local CoinManager deduction is
// silently reverted the next time any backend response syncs the balance
// (ShopHandler / GiftFlow both call CoinManager.SetBalance with the server's
// number).
//
// So this is the one sanctioned debit surface. It is a thin wrapper over the
// endpoints that already exist:
//
//   Charge -> POST /api/spend            (the same idempotent debit the shop uses;
//                                         the backend validates the sku by regex,
//                                         not against a catalog, so an ecosystem
//                                         sku like `ls_duel_wager` works as-is)
//   Credit -> POST /api/admin/grant      (free-form adjustment; its own docstring
//                                         names "event prizes, refunds" as the use
//                                         case, and the plugin already holds the
//                                         bearer token it requires)
//
// Guardrails kept intact
// ----------------------
//  * SERVER ONLY. Every entry point refuses off the server/host, so a client can
//    never drive its own balance.
//  * Callers are addressed by PLAYER NAME, never Steam64. Sibling mods stay out
//    of identity handling entirely; resolution to a Steam64/PlayFab id happens
//    here, from the connected peer list.
//  * The sku is namespaced and logged, so every ecosystem debit is auditable in
//    `spends` alongside shop purchases.
//  * This sells nothing. It moves currency between players who wagered it (and
//    refunds what a cancelled event took back). It does not create a way to buy
//    power — see docs/ecosystem/donation-hooks.md rule 1.
//
// Called by reflection (soft dependency), so a sibling mod needs no assembly
// reference to this one and degrades gracefully when donations isn't installed.
// The shape is therefore load-bearing: keep the names and signatures stable.
public static class ValcoinWallet
{
    /// Sku prefix every ecosystem debit must carry, so wagers can never be
    /// confused with shop purchases in the ledger.
    public const string SkuPrefix = "eco_";

    private class SpendResp { public string status; public int balance; public int spent; }
    private class GrantResp { public string status; public long grant_id; public int coins; }

    /// True when this instance can actually move coins: it is the server and the
    /// backend is configured. A sibling mod should gate its currency option on
    /// this and say why when it is false (see UnavailableReason).
    public static bool Ready => IsServer && Config.Ready;

    public static string UnavailableReason
    {
        get
        {
            if (!IsServer) return "Valcoin wagers are settled on the server.";
            if (!Config.Ready) return "The donation backend is not configured on this server.";
            return null;
        }
    }

    private static bool IsServer => ZNet.instance != null && ZNet.instance.IsServer();

    /// Best-known balance for a connected player, or -1 when it cannot be
    /// answered (offline, unresolvable id, or simply not in the local cache).
    /// -1 is deliberately distinct from 0: "unknown" must not read as "broke",
    /// which is the bug ShopHandler documents in its own pre-check.
    public static int BalanceOf(string playerName)
    {
        if (!IsServer) return -1;
        var id = Resolve(playerName);
        if (string.IsNullOrEmpty(id)) return -1;
        return CoinManager.TryGetKnownBalance(id, out var bal) ? bal : -1;
    }

    /// Debit `coins` from a connected player. `done(ok, message)` fires on the
    /// main thread once the backend has answered; `ok == false` means nothing was
    /// taken (the message is safe to show the player).
    ///
    /// The caller MUST NOT treat a call as settled until `done` reports true —
    /// the ledger is remote, and a wager that assumes success before the answer
    /// arrives can hand out a prize nobody paid for.
    public static void Charge(string playerName, string sku, int coins, string reason, Action<bool, string> done)
    {
        if (!Guard(coins, done, out var id, playerName, out var safeSku, sku)) return;

        // Cheap local veto, same rule as the shop: only refuse on a balance we
        // actually hold. An unknown balance goes to the backend, which owns the
        // ledger and answers 402 itself.
        if (CoinManager.TryGetKnownBalance(id, out int local) && local < coins)
        {
            done?.Invoke(false, $"Not enough Valcoins ({local} / {coins}).");
            return;
        }

        var key = $"eco-{safeSku}-{Guid.NewGuid():N}";
        SharedCoroutineRunner.Instance.StartCoroutine(BackendClient.Post<SpendResp>(
            "/api/spend",
            new
            {
                steam64 = id,
                sku = safeSku,
                coins,
                idempotency_key = key,
                metadata = new { source = "ecosystem", reason = reason ?? "", player = playerName ?? "" },
            },
            (ok, r, err) =>
            {
                if (!ok || r == null)
                {
                    if (err != null && err.Contains("402"))
                        done?.Invoke(false, "You do not have enough Valcoins.");
                    else
                        done?.Invoke(false, $"Valcoin charge failed. ({err ?? "unknown"})");
                    return;
                }
                CoinManager.SetBalance(id, r.balance);
                CoinManager.Save();
                Debug.Log($"[Valcoin] ecosystem charge: {coins} from {playerName} ({safeSku}); balance {r.balance}.");
                done?.Invoke(true, $"-{coins} Valcoins ({reason ?? safeSku}). Balance: {r.balance}");
            }));
    }

    /// Credit `coins` to a connected player — a wager payout or a refund of a
    /// charge this API took. Lands through the normal grant pipeline, so the
    /// player gets the usual "+N Valcoins!" toast on GrantPoller's next tick and
    /// the local balance cache stays correct without us writing it.
    public static void Credit(string playerName, string sku, int coins, string reason, Action<bool, string> done)
    {
        if (!Guard(coins, done, out var id, playerName, out var safeSku, sku)) return;

        SharedCoroutineRunner.Instance.StartCoroutine(BackendClient.Post<GrantResp>(
            "/api/admin/grant",
            new
            {
                steam64 = id,
                coins,
                note = $"{safeSku}: {reason ?? "ecosystem payout"} ({playerName})",
            },
            (ok, r, err) =>
            {
                if (!ok || r == null)
                {
                    Debug.LogWarning($"[Valcoin] ecosystem credit FAILED for {playerName} ({safeSku}, {coins}): {err ?? "unknown"}");
                    done?.Invoke(false, $"Valcoin payout failed. ({err ?? "unknown"})");
                    return;
                }
                Debug.Log($"[Valcoin] ecosystem credit: {coins} to {playerName} ({safeSku}).");
                done?.Invoke(true, $"+{coins} Valcoins ({reason ?? safeSku})");
            }));
    }

    // Shared preflight for both directions: server + backend + amount + sku
    // validation, and identity resolution. Returns false having already reported
    // the reason through `done`.
    private static bool Guard(int coins, Action<bool, string> done, out string id,
        string playerName, out string safeSku, string sku)
    {
        id = null;
        safeSku = null;

        if (!IsServer) { done?.Invoke(false, "Valcoin wagers are settled on the server."); return false; }
        if (!Config.Ready) { done?.Invoke(false, "The donation backend is not configured on this server."); return false; }
        if (coins <= 0) { done?.Invoke(false, "Amount must be positive."); return false; }

        safeSku = SanitizeSku(sku);
        if (safeSku == null) { done?.Invoke(false, "Invalid wager id."); return false; }

        id = Resolve(playerName);
        if (string.IsNullOrEmpty(id))
        {
            done?.Invoke(false, $"Couldn't resolve {playerName ?? "that player"}'s account — are they still connected?");
            return false;
        }
        return true;
    }

    // The backend accepts 2-32 chars of [a-z0-9_]. Normalise here rather than
    // trusting the caller, so a stray character is a clean refusal instead of a
    // 400 from the ledger.
    private static string SanitizeSku(string sku)
    {
        if (string.IsNullOrWhiteSpace(sku)) return null;
        var lowered = sku.Trim().ToLowerInvariant();
        if (!lowered.StartsWith(SkuPrefix)) lowered = SkuPrefix + lowered;
        var cleaned = new string(lowered.Where(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_').ToArray());
        if (cleaned.Length < 2) return null;
        return cleaned.Length > 32 ? cleaned.Substring(0, 32) : cleaned;
    }

    private static string Resolve(string playerName)
    {
        if (string.IsNullOrEmpty(playerName) || ZNet.instance == null) return null;
        var peer = ZNet.instance.GetConnectedPeers().FirstOrDefault(p =>
            p.m_playerName != null && p.m_playerName.Equals(playerName, StringComparison.OrdinalIgnoreCase));
        if (peer != null) return SteamIdResolver.FromPeer(peer);

        // The listen host is not in its own connected-peer list.
        var local = Player.m_localPlayer;
        if (local != null && string.Equals(local.GetPlayerName(), playerName, StringComparison.OrdinalIgnoreCase))
            return LocalIdentity.Steam64();

        return null;
    }
}
