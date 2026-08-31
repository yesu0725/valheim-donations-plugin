using System;
using System.Linq;

// Server-side handler for actions arriving from the in-game GUI panel.
//
// Action wire format is a colon-separated string:
//   "donate"
//   "quest:<questId>"                     — a ServerGuide quest just completed
//   "buy:<sku>"
//   "buyfail:<sku>"                       — the buyer's client couldn't apply an
//                                           armor aura it just paid for; refunds
//                                           against a one-shot server-side ticket
//   "gift:<playerName>:<amount>"
//   "title:<text>"
//   "title:clear"
//   "topdonors"
//   "whoami"                              — replies "__ADMIN__:true|false"
//   "admin_give:<playerName>:<amount>"    — admin only
//   "admin_remove:<playerName>:<amount>"  — admin only
//
// All replies that should be visible inside the panel come back via
// RpcLayer.PushPanelMessage (which the client UI listens for). Messages
// prefixed "__ADMIN__:" are a control signal, not a chat line — the client
// intercepts them instead of displaying them (see DonationPanel.AddLog).
public static class UiActionRouter
{
    public static void Execute(long senderPeerID, string action)
    {
        if (string.IsNullOrEmpty(action)) return;

        var peer = ZNet.instance?.GetPeer(senderPeerID);
        if (peer == null) return;

        string steam64 = SteamIdResolver.FromPeer(peer);
        string senderName = peer.m_playerName;

        // Reply helper — sends a line back to the requesting panel only.
        void Reply(string msg) => RpcLayer.PushPanelMessage(senderPeerID, msg);

        // Split "key:rest" once. Rest may contain further colons (e.g. shout text).
        string key, rest;
        int colon = action.IndexOf(':');
        if (colon < 0) { key = action; rest = ""; }
        else { key = action.Substring(0, colon); rest = action.Substring(colon + 1); }

        switch (key)
        {
            case "donate":       DonateFlow.Run(steam64, senderName, Reply); break;
            case "quest":        QuestFlow.Run(senderPeerID, steam64, senderName, rest.Trim(), Reply); break;
            case "buy":          DoBuy(steam64, rest, Reply); break;
            case "buyfail":      ShopHandler.ReportApplyFailed(steam64, rest.Trim().ToLowerInvariant(), m => Reply(m)); break;
            case "gift":         DoGift(steam64, senderName, rest, Reply); break;
            case "topdonors":    TopDonorsFetcher.Fetch(reply => Reply(reply)); break;
            case "whoami":       Reply("__ADMIN__:" + IsAdmin(steam64).ToString().ToLowerInvariant()); break;
            case "admin_give":   DoAdminAdjust(steam64, rest, Reply, give: true); break;
            case "admin_remove": DoAdminAdjust(steam64, rest, Reply, give: false); break;
            default:             Reply($"⚠️ Unknown UI action: {key}"); break;
        }
    }

    // "buy:<sku>" or "buy:<sku>:<arg>" (arg = armor slot for armor_vfx SKUs).
    private static void DoBuy(string steam64, string rest, Action<string> reply)
    {
        var parts = rest.Split(new[] { ':' }, 2);
        string sku = parts[0].Trim().ToLowerInvariant();
        string arg = parts.Length > 1 ? parts[1].Trim().ToLowerInvariant() : null;
        // reply is an Action<string>; ShopHandler.Buy wants its TellFn delegate.
        ShopHandler.Buy(steam64, sku, m => reply(m), extra: arg);
    }

    private static bool IsAdmin(string steam64) =>
        !string.IsNullOrEmpty(steam64) && Plugin.AdminSteamIDs.Contains(steam64);

    private static void DoAdminAdjust(string callerSteam64, string rest, Action<string> reply, bool give)
    {
        if (!IsAdmin(callerSteam64)) { reply("You are not authorized."); return; }

        var parts = rest.Split(new[] { ':' }, 2);
        if (parts.Length != 2 || !int.TryParse(parts[1].Trim(), out int amount) || amount <= 0)
        { reply("Bad amount."); return; }

        string targetName = parts[0].Trim();
        if (!ResolveTargetByName(targetName, out var targetSteam64, out var targetPlayer))
        { reply($"Player \"{targetName}\" not found or no Steam ID."); return; }

        if (!Config.Ready)
        {
            reply("The donation backend isn't configured on this server, so balances can't be adjusted.");
            return;
        }

        if (give) AdminGive(targetSteam64, targetName, targetPlayer, amount, reply);
        else      AdminRemove(targetSteam64, targetName, targetPlayer, amount, reply);
    }

    private class AdminGrantResp { public string status; public long grant_id; public int coins; }
    private class AdminSpendResp { public string status; public int balance; public int spent; }

    // Admin adjustments go through the BACKEND, which owns the ledger.
    //
    // They used to write CoinManager and nothing else. That cache is not the
    // ledger — the panel, the shop and every other client read the backend — so
    // admin-granted coins existed only in one JSON file on one server and were
    // invisible everywhere they mattered: the player's own panel never showed
    // them, and a purchase against them was refused for insufficient funds by a
    // backend that had never heard of the credit. Test credits handed out this way
    // looked real for as long as nobody tried to spend them.
    //
    // Credit rides /api/admin/grant, the same free-form adjustment endpoint the
    // ecosystem wallet uses for payouts, so the coins arrive through the normal
    // grant pipeline (GrantPoller toast + cache reconcile, see ValcoinWallet.Credit).
    private static void AdminGive(string steam64, string name, Player player, int amount, Action<string> reply)
    {
        SharedCoroutineRunner.Instance.StartCoroutine(BackendClient.Post<AdminGrantResp>(
            "/api/admin/grant",
            new { steam64, coins = amount, note = $"admin_give: {name}" },
            (ok, r, err) =>
            {
                if (!ok || r == null)
                {
                    reply($"Couldn't give {amount} to {name} — the ledger refused it. ({err ?? "unknown"})");
                    return;
                }
                // No local write and no toast here: the grant is now pending on the
                // backend, and GrantPoller delivers it (and the "+N Valcoins!" toast)
                // on its next tick, exactly like a donation or a quest payout.
                reply($"Gave {amount} Valcoins to {name}. It lands on their next grant tick.");
            }));
    }

    // Debit rides /api/spend — the same atomic, idempotency-keyed debit the shop
    // uses. Recording a removal as a spend keeps it auditable in the ledger next to
    // every other debit, rather than being a silent edit of a cache file.
    private static void AdminRemove(string steam64, string name, Player player, int amount, Action<string> reply)
    {
        var key = $"adminrm-{Guid.NewGuid():N}";
        SharedCoroutineRunner.Instance.StartCoroutine(BackendClient.Post<AdminSpendResp>(
            "/api/spend",
            new
            {
                steam64,
                sku = "admin_remove",
                coins = amount,
                idempotency_key = key,
                metadata = new { source = "admin", player = name ?? "" },
            },
            (ok, r, err) =>
            {
                if (!ok || r == null)
                {
                    reply(err != null && err.Contains("402")
                        ? $"{name} doesn't have {amount} Valcoins to remove."
                        : $"Couldn't remove {amount} from {name}. ({err ?? "unknown"})");
                    return;
                }
                CoinManager.SetBalance(steam64, r.balance);
                reply($"Removed {amount} from {name} (new balance: {r.balance}).");
                player?.Message(MessageHud.MessageType.TopLeft, $"{amount} Valcoins removed by admin.");
            }));
    }

    private static bool ResolveTargetByName(string name, out string steam64, out Player player)
    {
        steam64 = null; player = null;
        if (ZNet.instance == null) return false;

        var peer = ZNet.instance.GetConnectedPeers().FirstOrDefault(p =>
            p.m_playerName != null && p.m_playerName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (peer == null) return false;

        steam64 = SteamIdResolver.FromPeer(peer);
        if (string.IsNullOrEmpty(steam64)) return false;

        player = Player.GetAllPlayers().FirstOrDefault(pp =>
            pp.GetPlayerName().Equals(name, StringComparison.OrdinalIgnoreCase));
        return true;
    }

    // ─── Action implementations — re-using existing handlers where possible ─

    private static void DoGift(string fromSteam64, string fromName, string rest, Action<string> reply)
    {
        // Expected: "<playerName>:<amount>"
        var parts = rest.Split(new[] { ':' }, 2);
        if (parts.Length != 2) { reply("Bad gift format."); return; }
        if (!int.TryParse(parts[1].Trim(), out int amount) || amount <= 0)
        { reply("Amount must be a positive number."); return; }

        GiftFlow.Run(fromSteam64, fromName, parts[0].Trim(), amount, reply);
    }
}
