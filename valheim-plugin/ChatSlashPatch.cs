using HarmonyLib;
using UnityEngine;
using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;

// Server-side chat-slash interceptor (Phase 1+).
// Hooks Chat.RPC_ChatMessage so vanilla and modded clients use the same path.
//
// Commands:
//   /coins                                       — anyone
//   /donate                                      — anyone (mints a claim code)
//   /shop                                        — list catalog + your balance
//   /buy <sku>                                   — purchase a SKU
//   /gift <player> <amount>                      — transfer Valcoins
//   /title <text|clear>                          — set chat title (needs chat_title perk)
//   /givecoins <player> <amount>                 — admin only
//   /removecoins <player> <amount>               — admin only
[HarmonyPatch]
public static class ChatRpcSlashPatch
{
    static MethodBase TargetMethod()
    {
        return typeof(Chat)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(m => m.Name == "RPC_ChatMessage");
    }

    static bool Prefix(MethodBase __originalMethod, object[] __args)
    {
        try
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer())
                return true;

            int textIdx = -1;
            string text = null;
            string networkUserId = null;
            string senderName = null;
            long sender = 0;

            var ps = __originalMethod.GetParameters();
            for (int i = 0; i < ps.Length; i++)
            {
                var name = ps[i].Name;
                var val  = __args[i];
                switch (name)
                {
                    case "text":   text = val as string; textIdx = i; break;
                    case "sender": if (val is long l) sender = l; break;
                    case "senderNetworkUserId": networkUserId = val as string; break;
                    case "userInfo":
                        if (val != null)
                        {
                            var t = val.GetType();
                            senderName = (t.GetField("Name")?.GetValue(val) as string)
                                         ?? (t.GetProperty("Name")?.GetValue(val) as string)
                                         ?? senderName;
                            networkUserId = (t.GetField("NetworkUserId")?.GetValue(val) as string)
                                            ?? (t.GetProperty("NetworkUserId")?.GetValue(val) as string)
                                            ?? networkUserId;
                        }
                        break;
                }
            }

            if (string.IsNullOrEmpty(text)) return true;

            string steam64 = SteamIdResolver.FromNetworkUserId(networkUserId)
                              ?? SteamIdResolver.FromPeerId(sender);

            // Non-slash chat message — decorate with badge/title and let it through.
            if (text[0] != '/')
            {
                if (textIdx >= 0)
                {
                    var prefix = BuildChatPrefix(steam64);
                    if (prefix != null) __args[textIdx] = prefix + text;
                }
                return true;
            }

            var senderPlayer = !string.IsNullOrEmpty(steam64)
                ? SteamIdResolver.OnlinePlayerFor(steam64)
                : null;
            if (senderPlayer == null && !string.IsNullOrEmpty(senderName))
            {
                senderPlayer = Player.GetAllPlayers().FirstOrDefault(p =>
                    p.GetPlayerName().Equals(senderName, StringComparison.OrdinalIgnoreCase));
            }

            var parts = text.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            string cmd = parts[0].ToLowerInvariant();
            string rest = parts.Length > 1 ? parts[1].Trim() : "";
            string[] args = rest.Length == 0
                ? Array.Empty<string>()
                : rest.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            switch (cmd)
            {
                case "/coins":       HandleCoins(steam64, senderPlayer); return false;
                case "/donate":      DonateFlow.Run(steam64, senderName, m => Tell(senderPlayer, m)); return false;
                case "/topdonors":   TopDonorsFetcher.Fetch(m => Tell(senderPlayer, m)); return false;

                case "/shop":        HandleShop(steam64, senderPlayer); return false;
                case "/buy":         HandleBuy(steam64, senderPlayer, args); return false;
                case "/gift":        HandleGift(steam64, senderName, senderPlayer, args); return false;
                case "/title":       HandleTitle(steam64, senderPlayer, rest); return false;

                case "/givecoins":   HandleGiveCoins(steam64, senderPlayer, args); return false;
                case "/removecoins": HandleRemoveCoins(steam64, senderPlayer, args); return false;

                default: return true; // not ours
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Valcoin] ChatRpcSlashPatch error: {ex}");
            return true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Chat decoration
    // ─────────────────────────────────────────────────────────────────────

    // Use only non-creating PerkManager lookups so we don't bloat perks.json
    // with an empty row for every player that ever opens chat.
    private static string BuildChatPrefix(string steam64)
    {
        if (string.IsNullOrEmpty(steam64)) return null;
        bool hasBadge = PerkManager.Has(steam64, "donor_badge");
        string title  = PerkManager.Has(steam64, "chat_title") ? PerkManager.Title(steam64) : null;
        if (!hasBadge && string.IsNullOrEmpty(title)) return null;

        var sb = new System.Text.StringBuilder(8 + (title?.Length ?? 0));
        if (hasBadge) sb.Append("⭐ ");
        if (!string.IsNullOrEmpty(title)) sb.Append('[').Append(title).Append("] ");
        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Coin / donate
    // ─────────────────────────────────────────────────────────────────────

    private static void HandleCoins(string steam64, Player p)
    {
        if (string.IsNullOrEmpty(steam64)) { Tell(p, "⚠️ Couldn't resolve your Steam ID."); return; }
        int bal = CoinManager.GetBalance(steam64);
        var perks = PerkManager.Get(steam64);
        var line = $"💰 You have {bal} Valcoins.";
        if (perks.perks.Count > 0) line += "  Perks: " + string.Join(", ", perks.perks);
        Tell(p, line);
    }

    // /donate now lives in DonateFlow (shared with the GUI panel's action router).

    // ─────────────────────────────────────────────────────────────────────
    // Shop / buy
    // ─────────────────────────────────────────────────────────────────────

    private static void HandleShop(string steam64, Player p)
    {
        if (Catalog.Order.Count == 0) { Tell(p, "🛒 The shop is empty. Ask an admin to set up valcoin_shop.yaml."); return; }
        int bal = string.IsNullOrEmpty(steam64) ? 0 : CoinManager.GetBalance(steam64);
        Tell(p, $"🛒 Shop  (your balance: {bal} Valcoins)");
        foreach (var sku in Catalog.Order)
        {
            string ownership = "";
            if (sku.Effect == "grant_item")
            {
                if (sku.WeeklyCap > 0) ownership += $"  [max {sku.WeeklyCap}/week]";
                if (!string.IsNullOrEmpty(sku.RequiresBoss)) ownership += $"  [needs {sku.RequiresBoss}]";
            }
            else if (!string.IsNullOrEmpty(steam64))
            {
                if (sku.Effect == "grant_perk" && PerkManager.Has(steam64, sku.Perk))
                    ownership = "  [owned]";
                else if (sku.Effect == "add_charges")
                    ownership = $"  [you have {PerkManager.Charges(steam64, sku.Perk)}]";
            }
            Tell(p, $"  {sku.Id}  ·  {sku.Price}c  ·  {sku.Name}{ownership}");
            if (!string.IsNullOrEmpty(sku.Description))
                Tell(p, $"      {sku.Description}");
        }
        Tell(p, "Buy with: /buy <id>");
    }

    private static void HandleBuy(string steam64, Player p, string[] args)
    {
        if (args.Length != 1) { Tell(p, "⚠️ Usage: /buy <sku>  (see /shop)"); return; }
        var capturedPlayer = p;
        ShopHandler.Buy(steam64, args[0].ToLowerInvariant(), msg => Tell(capturedPlayer, msg));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Gift
    // ─────────────────────────────────────────────────────────────────────

    private static void HandleGift(string fromSteam64, string fromName, Player fromPlayer, string[] args)
    {
        if (args.Length != 2 || !int.TryParse(args[1], out int amount))
        { Tell(fromPlayer, "⚠️ Usage: /gift <playerName> <amount>"); return; }
        GiftFlow.Run(fromSteam64, fromName, args[0], amount, m => Tell(fromPlayer, m));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Title
    // ─────────────────────────────────────────────────────────────────────

    private static void HandleTitle(string steam64, Player p, string rest)
    {
        if (string.IsNullOrEmpty(steam64)) { Tell(p, "⚠️ Couldn't resolve your Steam ID."); return; }
        if (!PerkManager.Has(steam64, "chat_title")) { Tell(p, "🔒 You need the \"chat_title\" perk. /buy chat_title"); return; }

        if (string.IsNullOrEmpty(rest))
        {
            var cur = PerkManager.Title(steam64);
            Tell(p, string.IsNullOrEmpty(cur)
                ? "Usage: /title <text>  (or /title clear)"
                : $"Your title is currently: [{cur}]   Change with /title <text>, remove with /title clear");
            return;
        }

        if (rest.Equals("clear", StringComparison.OrdinalIgnoreCase) || rest == "-")
        {
            PerkManager.SetTitle(steam64, null);
            Tell(p, "✅ Title cleared.");
            return;
        }

        if (rest.Length > 16) { Tell(p, "⚠️ Title must be 16 characters or fewer."); return; }
        if (!System.Text.RegularExpressions.Regex.IsMatch(rest, @"^[\w \-'\.!?]+$"))
        { Tell(p, "⚠️ Letters, numbers and basic punctuation only."); return; }

        PerkManager.SetTitle(steam64, rest);
        Tell(p, $"✅ Title set to [{rest}].");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Admin
    // ─────────────────────────────────────────────────────────────────────

    private static void HandleGiveCoins(string callerSteam64, Player caller, string[] args)
    {
        if (string.IsNullOrEmpty(callerSteam64) || !Plugin.AdminSteamIDs.Contains(callerSteam64))
        { Tell(caller, "🚫 You are not authorized."); return; }

        if (args.Length != 2 || !int.TryParse(args[1], out int amount) || amount <= 0)
        { Tell(caller, "⚠️ Usage: /givecoins <playerName> <amount>"); return; }

        if (!ResolveTargetByName(args[0], out var targetSteam64, out var targetPlayer))
        { Tell(caller, "⚠️ Player not found or their Steam ID couldn't be resolved."); return; }

        CoinManager.AddCoins(targetSteam64, amount);
        Tell(caller, $"✅ Gave {amount} Valcoins to {args[0]}.");
        if (targetPlayer != null)
            targetPlayer.Message(MessageHud.MessageType.TopLeft, $"🎁 +{amount} Valcoins from admin!");
    }

    private static void HandleRemoveCoins(string callerSteam64, Player caller, string[] args)
    {
        if (string.IsNullOrEmpty(callerSteam64) || !Plugin.AdminSteamIDs.Contains(callerSteam64))
        { Tell(caller, "🚫 You are not authorized."); return; }

        if (args.Length != 2 || !int.TryParse(args[1], out int amount) || amount <= 0)
        { Tell(caller, "⚠️ Usage: /removecoins <playerName> <amount>"); return; }

        if (!ResolveTargetByName(args[0], out var targetSteam64, out var targetPlayer))
        { Tell(caller, "⚠️ Player not found or their Steam ID couldn't be resolved."); return; }

        int newBal = Math.Max(0, CoinManager.GetBalance(targetSteam64) - amount);
        CoinManager.SetBalance(targetSteam64, newBal);
        Tell(caller, $"✅ Removed {amount} from {args[0]} (new balance: {newBal}).");
        if (targetPlayer != null)
            targetPlayer.Message(MessageHud.MessageType.TopLeft, $"❌ {amount} Valcoins removed by admin.");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────

    private static bool ResolveTargetByName(string name, out string steam64, out Player player)
    {
        steam64 = null; player = null;
        if (ZNet.instance == null) return false;

        var peer = ZNet.instance.GetConnectedPeers().FirstOrDefault(p =>
            p.m_playerName != null &&
            p.m_playerName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (peer == null) return false;

        steam64 = SteamIdResolver.FromPeer(peer);
        if (string.IsNullOrEmpty(steam64)) return false;

        player = Player.GetAllPlayers().FirstOrDefault(pp =>
            pp.GetPlayerName().Equals(name, StringComparison.OrdinalIgnoreCase));
        return true;
    }

    private static void Tell(Player player, string msg)
    {
        if (player != null) player.Message(MessageHud.MessageType.TopLeft, msg);
        else Debug.Log($"[Valcoin] {msg}");
    }

}

// Backend HTTP calls run as coroutines, but the chat patch is a static method
// without a MonoBehaviour. This holder gives static handlers a place to spawn
// coroutines from.
public class SharedCoroutineRunner : MonoBehaviour
{
    private static SharedCoroutineRunner _instance;
    public static SharedCoroutineRunner Instance
    {
        get
        {
            if (_instance == null)
            {
                var go = new GameObject("ValcoinCoroutineRunner");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _instance = go.AddComponent<SharedCoroutineRunner>();
            }
            return _instance;
        }
    }
}
