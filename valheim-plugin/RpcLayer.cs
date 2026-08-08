using System;
using System.Collections;
using UnityEngine;

// Silent (non-chat) bridge between the in-game GUI panel and the server.
// This is the ONLY way donation actions reach the server — there is no chat
// or console command path (see docs/PLUGIN.md for why that was removed).
//
// Two RPCs:
//   "vc_action"  — client → server. Asks the server to run an action
//                  ("donate", "buy:donor_badge", etc.) triggered by a button
//                  in the F4 Codex or F8 panel, without touching public chat.
//   "vc_panel"   — server → client. Pushes a free-form text blob the client
//                  can display in its panel (used for donate-code responses).
//
// Why not just submit chat text from the panel? Because then the player's
// donation code or buy action would appear in the public chat history,
// visible to anyone scrolling back. Silent RPCs avoid that.
public static class RpcLayer
{
    public const string ActionRpc  = "vc_action";
    public const string PanelRpc   = "vc_panel";
    public const string CatalogRpc = "vc_catalog";
    public const string QuestsRpc  = "vc_quests";
    public const string QuestAckRpc = "vc_questack";

    private static bool _registeredServer;
    private static bool _registeredClient;

    // Client-side callback the UI registers to receive panel messages.
    public static Action<string> OnPanelMessage;

    public static IEnumerator RegisterWhenReady(bool serverSide)
    {
        while (ZRoutedRpc.instance == null) yield return null;

        if (serverSide && !_registeredServer)
        {
            ZRoutedRpc.instance.Register<string>(ActionRpc, HandleActionOnServer);
            _registeredServer = true;
            Debug.Log("[Valcoin] RPC registered (server): " + ActionRpc);
        }
        if (!serverSide && !_registeredClient)
        {
            ZRoutedRpc.instance.Register<string>(PanelRpc, HandlePanelOnClient);
            ZRoutedRpc.instance.Register<string>(CatalogRpc, HandleCatalogOnClient);
            ZRoutedRpc.instance.Register<string>(QuestsRpc, HandleQuestsOnClient);
            ZRoutedRpc.instance.Register<string>(QuestAckRpc, HandleQuestAckOnClient);
            _registeredClient = true;
            Debug.Log("[Valcoin] RPC registered (client): " + PanelRpc + ", " + CatalogRpc
                      + ", " + QuestsRpc + ", " + QuestAckRpc);
        }
    }

    // ─── Client → server ────────────────────────────────────────────────

    public static void SendAction(string action)
    {
        if (ZRoutedRpc.instance == null) return;
        try
        {
            // ZRoutedRpc.Everybody routes to all; the server-side handler
            // checks IsServer() and only acts there.
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, ActionRpc, action);
        }
        catch (Exception ex)
        {
            Debug.LogError("[Valcoin] SendAction failed: " + ex.Message);
        }
    }

    private static void HandleActionOnServer(long senderPeerID, string action)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
        try
        {
            UiActionRouter.Execute(senderPeerID, action);
        }
        catch (Exception ex)
        {
            Debug.LogError("[Valcoin] HandleActionOnServer error: " + ex);
        }
    }

    // ─── Server → client ────────────────────────────────────────────────

    public static void PushPanelMessage(long peerID, string msg)
    {
        if (ZRoutedRpc.instance == null) return;
        try { ZRoutedRpc.instance.InvokeRoutedRPC(peerID, PanelRpc, msg); }
        catch (Exception ex) { Debug.LogError("[Valcoin] PushPanelMessage failed: " + ex.Message); }
    }

    private static void HandlePanelOnClient(long _from, string msg)
    {
        if (ZNet.instance != null && ZNet.instance.IsServer()) return;
        try { OnPanelMessage?.Invoke(msg); }
        catch (Exception ex) { Debug.LogError("[Valcoin] OnPanelMessage handler failed: " + ex); }
    }

    // ─── Server → client: catalog sync (Phase 3) ───────────────────────────

    public static void BroadcastCatalog(string json)
    {
        if (ZRoutedRpc.instance == null || string.IsNullOrEmpty(json)) return;
        try { ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, CatalogRpc, json); }
        catch (Exception ex) { Debug.LogError("[Valcoin] BroadcastCatalog failed: " + ex.Message); }
    }

    private static void HandleCatalogOnClient(long _from, string json)
    {
        if (ZNet.instance != null && ZNet.instance.IsServer()) return;
        try { Catalog.ApplyRemote(json); }
        catch (Exception ex) { Debug.LogError("[Valcoin] Catalog apply failed: " + ex); }
    }

    // ─── Server → client: quest list sync ──────────────────────────────────
    //
    // Tells clients which "VC.Q.*" player keys QuestWatcher should watch for.
    // valcoin_quests.yaml only exists on the server, so without this a remote
    // client would never report a completed quest.

    public static void BroadcastQuests(string json)
    {
        if (ZRoutedRpc.instance == null || string.IsNullOrEmpty(json)) return;
        try { ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, QuestsRpc, json); }
        catch (Exception ex) { Debug.LogError("[Valcoin] BroadcastQuests failed: " + ex.Message); }
    }

    private static void HandleQuestsOnClient(long _from, string json)
    {
        if (ZNet.instance != null && ZNet.instance.IsServer()) return;
        try { QuestCatalog.ApplyRemote(json); }
        catch (Exception ex) { Debug.LogError("[Valcoin] Quest catalog apply failed: " + ex); }
    }

    // ─── Server → client: quest report acknowledged ────────────────────────
    //
    // Sent only once the server has a DEFINITIVE answer from the backend
    // (credited / already claimed / capped). Until this arrives the client
    // keeps the "VC.Q.<id>" key and re-reports, so a server that can't handle
    // the report — an old build with no quest handler, a backend outage, a
    // quest with no price — postpones the payout instead of destroying it.

    public static void SendQuestAck(long peerID, string questId)
    {
        if (ZRoutedRpc.instance == null || string.IsNullOrEmpty(questId)) return;
        try { ZRoutedRpc.instance.InvokeRoutedRPC(peerID, QuestAckRpc, questId); }
        catch (Exception ex) { Debug.LogError("[Valcoin] SendQuestAck failed: " + ex.Message); }
    }

    private static void HandleQuestAckOnClient(long _from, string questId)
    {
        try { QuestWatcher.OnAck(questId); }
        catch (Exception ex) { Debug.LogError("[Valcoin] Quest ack handler failed: " + ex); }
    }
}
