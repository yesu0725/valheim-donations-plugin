using System;
using System.Linq;

// Server-side handler for actions arriving from the in-game GUI panel.
//
// Action wire format is a colon-separated string:
//   "donate"
//   "buy:<sku>"
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
            case "buy":          DoBuy(steam64, rest, Reply); break;
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

        if (give)
        {
            CoinManager.AddCoins(targetSteam64, amount);
            reply($"Gave {amount} Valcoins to {targetName}.");
            targetPlayer?.Message(MessageHud.MessageType.TopLeft, $"+{amount} Valcoins from admin!");
        }
        else
        {
            int newBal = Math.Max(0, CoinManager.GetBalance(targetSteam64) - amount);
            CoinManager.SetBalance(targetSteam64, newBal);
            reply($"Removed {amount} from {targetName} (new balance: {newBal}).");
            targetPlayer?.Message(MessageHud.MessageType.TopLeft, $"{amount} Valcoins removed by admin.");
        }
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
