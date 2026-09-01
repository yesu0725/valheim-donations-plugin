using System;
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

    // WHICH ZRoutedRpc we registered on -- not merely whether we ever did.
    //
    // THE RELOG BUG THIS EXISTS TO KILL. These were two plain bools. ZRoutedRpc
    // is built fresh by ZNet every time you enter a world and thrown away when
    // you log out to the main menu, but the plugin is loaded once for the whole
    // process, so the bools stayed true across that boundary while the handler
    // table they described no longer existed. Second and every later session,
    // Register looked at "already registered" and did nothing, leaving
    // the client with a live connection and NO handler for vc_panel, vc_catalog,
    // vc_quests or vc_questack.
    //
    // Outbound calls still worked -- InvokeRoutedRPC needs no registration -- so
    // a purchase reached the server and was DEBITED, and then every reply was
    // dropped on arrival: no __ARMORVFX__ (so an aura was paid for and never
    // applied), no __BUYRES__ verdict (so the panel sat on the working modal for
    // 40s and then called it a failure), and no buyfail from the client, so the
    // refund that exists for exactly this case never fired either. Quest acks
    // vanished the same way. Only a full game restart cleared it, because only
    // that reset these statics; restarting the server never did, because the
    // server's own ZRoutedRpc had not changed.
    //
    // Comparing against the CURRENT instance makes the registration a fact about
    // this session instead of about the process.
    private static ZRoutedRpc _serverRegisteredOn;
    private static ZRoutedRpc _clientRegisteredOn;

    // A listen-server host registers BOTH halves against the same instance, so
    // this tracks the announcement separately: one OnSessionStart per session,
    // not one per half.
    private static ZRoutedRpc _announcedOn;

    // Client-side callback the UI registers to receive panel messages.
    public static Action<string> OnPanelMessage;

    /// Raised the first time we register against a NEW ZRoutedRpc, i.e. once per
    /// world session. Anything holding per-session client state hangs off this
    /// so a relog starts clean (see DonationPanel, QuestWatcher).
    public static event Action OnSessionStart;

    /// The session ended (ZNet/ZRoutedRpc torn down). Drop the references so the
    /// dead instance isn't kept alive, and so the next session re-registers.
    public static void Forget()
    {
        if (_serverRegisteredOn == null && _clientRegisteredOn == null && _announcedOn == null)
            return;   // already clear; this is called every frame between sessions
        _serverRegisteredOn = null;
        _clientRegisteredOn = null;
        _announcedOn = null;
    }

    /// Idempotent per ZRoutedRpc instance: safe to call every frame. Called from
    /// Plugin.Update, which is what makes it survive a logout/login cycle.
    public static void Register(bool serverSide)
    {
        var rpc = ZRoutedRpc.instance;
        if (rpc == null) return;

        if (serverSide && !ReferenceEquals(_serverRegisteredOn, rpc))
        {
            rpc.Register<string>(ActionRpc, HandleActionOnServer);
            _serverRegisteredOn = rpc;
            Debug.Log("[Valcoin] RPC registered (server): " + ActionRpc);
        }
        if (!serverSide && !ReferenceEquals(_clientRegisteredOn, rpc))
        {
            rpc.Register<string>(PanelRpc, HandlePanelOnClient);
            rpc.Register<string>(CatalogRpc, HandleCatalogOnClient);
            rpc.Register<string>(QuestsRpc, HandleQuestsOnClient);
            rpc.Register<string>(QuestAckRpc, HandleQuestAckOnClient);
            _clientRegisteredOn = rpc;
            Debug.Log("[Valcoin] RPC registered (client): " + PanelRpc + ", " + CatalogRpc
                      + ", " + QuestsRpc + ", " + QuestAckRpc);
        }

        if (ReferenceEquals(_announcedOn, rpc)) return;
        _announcedOn = rpc;
        try { OnSessionStart?.Invoke(); }
        catch (Exception ex) { Debug.LogError("[Valcoin] session-start handler failed: " + ex); }
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

    // Guarded on IsDedicated, NOT IsServer -- and the difference is the whole
    // reason a listen-server host saw no reply to anything.
    //
    // A host IS a server, so `IsServer()` threw away every panel message aimed at
    // their own panel. The messages were being delivered correctly: PushPanelMessage
    // targets the host's own uid, and ZRoutedRpc runs a self-targeted call through
    // HandleRoutedRPC locally (`if (targetPeerID == m_id || targetPeerID == 0)`),
    // so the handler fired and then dropped the message on the floor. Only a
    // dedicated server genuinely has no panel to show it in.
    //
    // The catalog and quest-list handlers below keep the IsServer guard on
    // purpose: a host loaded both from its own YAML and is the authority on them,
    // so applying its own broadcast back to itself would be a pointless
    // round-trip through the wire format.
    private static void HandlePanelOnClient(long _from, string msg)
    {
        if (ZNet.instance != null && ZNet.instance.IsDedicated()) return;
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
