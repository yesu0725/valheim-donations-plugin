using System;
using System.Collections;
using UnityEngine;

// Silent (non-chat) bridge between the in-game GUI panel and the server.
//
// Two RPCs:
//   "vc_action"  — client → server. Asks the server to run a slash-command-
//                  equivalent action ("/donate", "/buy donor_badge", etc.)
//                  WITHOUT echoing it into the public chat log.
//   "vc_panel"   — server → client. Pushes a free-form text blob the client
//                  can display in its panel (used for /donate responses).
//
// Why not just submit chat text from the panel? Because then the player's
// donation code or buy command would appear in the public chat history,
// visible to anyone scrolling back. Silent RPCs avoid that.
public static class RpcLayer
{
    public const string ActionRpc  = "vc_action";
    public const string PanelRpc   = "vc_panel";
    public const string CatalogRpc = "vc_catalog";

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
            _registeredClient = true;
            Debug.Log("[Valcoin] RPC registered (client): " + PanelRpc + ", " + CatalogRpc);
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
}
