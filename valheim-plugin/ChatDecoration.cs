using HarmonyLib;
using System.Linq;
using System.Reflection;
using UnityEngine;

// Server-side chat decoration: prefixes a player's chat messages with their
// donor badge (⭐) and/or chat title ([Bracket]), if they own those perks.
//
// This is deliberately separate from command handling — donation actions
// (donate/shop/buy/gift/etc.) now go exclusively through the F4 Codex / F8
// panel's silent RPC (see UiActionRouter.cs). Chat-typed slash commands were
// removed because this reflection-based Chat.RPC_ChatMessage hook proved
// unreliable on a server running several other mods that also patch chat.
[HarmonyPatch]
public static class ChatDecorationPatch
{
    static MethodBase TargetMethod()
    {
        return typeof(Chat)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(m => m.Name == "RPC_ChatMessage");
    }

    static void Prefix(MethodBase __originalMethod, object[] __args)
    {
        try
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            int textIdx = -1;
            string text = null;
            string networkUserId = null;
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
                            networkUserId = (t.GetField("NetworkUserId")?.GetValue(val) as string)
                                            ?? (t.GetProperty("NetworkUserId")?.GetValue(val) as string)
                                            ?? networkUserId;
                        }
                        break;
                }
            }

            if (string.IsNullOrEmpty(text) || text[0] == '/' || textIdx < 0) return;

            string steam64 = SteamIdResolver.FromNetworkUserId(networkUserId)
                              ?? SteamIdResolver.FromPeerId(sender);
            if (string.IsNullOrEmpty(steam64)) return;

            bool hasBadge = PerkManager.Has(steam64, "donor_badge");
            string title  = PerkManager.Has(steam64, "chat_title") ? PerkManager.Title(steam64) : null;
            if (!hasBadge && string.IsNullOrEmpty(title)) return;

            var sb = new System.Text.StringBuilder(8 + (title?.Length ?? 0));
            if (hasBadge) sb.Append("⭐ ");
            if (!string.IsNullOrEmpty(title)) sb.Append('[').Append(title).Append("] ");
            __args[textIdx] = sb.ToString() + text;
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[Valcoin] ChatDecorationPatch error: {ex}");
        }
    }
}
