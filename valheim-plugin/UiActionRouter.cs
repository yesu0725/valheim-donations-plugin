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
//
// All replies that should be visible inside the panel come back via
// RpcLayer.PushPanelMessage (which the client UI listens for).
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
            case "donate":      DonateFlow.Run(steam64, senderName, Reply); break;
            case "buy":         ShopHandler.Buy(steam64, rest.Trim().ToLowerInvariant(), Reply); break;
            case "gift":        DoGift(steam64, senderName, rest, Reply); break;
            case "title":       DoTitle(steam64, rest, Reply); break;
            case "topdonors":   TopDonorsFetcher.Fetch(reply => Reply(reply)); break;
            default:            Reply($"⚠️ Unknown UI action: {key}"); break;
        }
    }

    // ─── Action implementations — re-using existing handlers where possible ─

    private static void DoGift(string fromSteam64, string fromName, string rest, Action<string> reply)
    {
        // Expected: "<playerName>:<amount>"
        var parts = rest.Split(new[] { ':' }, 2);
        if (parts.Length != 2) { reply("⚠️ Bad gift format."); return; }
        if (!int.TryParse(parts[1].Trim(), out int amount) || amount <= 0)
        { reply("⚠️ Amount must be a positive number."); return; }

        GiftFlow.Run(fromSteam64, fromName, parts[0].Trim(), amount, reply);
    }

    private static void DoTitle(string steam64, string rest, Action<string> reply)
    {
        if (string.IsNullOrEmpty(steam64)) { reply("⚠️ Couldn't resolve your Steam ID."); return; }
        if (!PerkManager.Has(steam64, "chat_title"))
        { reply("🔒 You need the \"chat_title\" perk. Buy it from the shop tab."); return; }

        if (rest.Equals("clear", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(rest))
        { PerkManager.SetTitle(steam64, null); reply("✅ Title cleared."); return; }

        if (rest.Length > 16) { reply("⚠️ 16 characters or fewer."); return; }
        if (!System.Text.RegularExpressions.Regex.IsMatch(rest, @"^[\w \-'\.!?]+$"))
        { reply("⚠️ Letters, numbers and basic punctuation only."); return; }

        PerkManager.SetTitle(steam64, rest);
        reply($"✅ Title set to [{rest}].");
    }
}
