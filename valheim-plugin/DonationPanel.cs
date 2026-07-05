using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json;

// Client-side in-game GUI for the donations system.
//
// Toggle with F8 (configurable via valcoin_config.json -> ui_toggle_key).
//
// Design goals:
//   * Zero external UI dependencies (no Jotunn). Uses Unity IMGUI.
//   * Single MonoBehaviour, ~350 LOC.
//   * Doesn't block gameplay — closes if any modal/menu opens.
//   * Sends silent RPCs instead of typing into public chat.
//
// Layout:
//   ┌─────── Valheim Donations ──── [X] ┐
//   │ Balance: 1500 c                   │
//   │ Perks: donor_badge, sethome       │
//   │                                   │
//   │ [Donate]  [Shop]  [Gift]  [Top]   │   <- tab strip
//   │ -----------------------------     │
//   │ < tab content >                   │
//   └───────────────────────────────────┘
public class DonationPanel : MonoBehaviour
{
    private const int   PanelW = 520;
    private const int   PanelH = 480;

    private enum Tab { Donate, Shop, Gift, Top }
    private Tab _tab = Tab.Donate;
    private bool _open;

    private KeyCode _toggleKey = KeyCode.F8;

    // Cached per-player state.
    private int     _balance;
    private List<string> _perks = new List<string>();
    private List<TopEntry> _topDonors = new List<TopEntry>();

    // Buffer for messages pushed by the server (donation codes, buy results, etc.).
    private readonly List<string> _log = new List<string>();
    private const int LogCap = 12;
    private Vector2 _logScroll;

    // Gift / shout text fields.
    private string _giftTo = "", _giftAmount = "", _titleText = "";

    private GUIStyle _bg, _hdr, _btn, _btnActive, _line, _logLine, _label;
    private bool _stylesReady;

    private float _lastStateFetch;
    private const float StateRefreshSeconds = 15f;

    // ─── lifecycle ────────────────────────────────────────────────────────

    private void Awake()
    {
        // Pull the configured key if set.
        if (!string.IsNullOrEmpty(Config.UiToggleKey)
            && Enum.TryParse<KeyCode>(Config.UiToggleKey, true, out var parsed))
            _toggleKey = parsed;

        RpcLayer.OnPanelMessage += AddLog;
        DontDestroyOnLoad(gameObject);
    }

    private void OnDestroy()
    {
        RpcLayer.OnPanelMessage -= AddLog;
        DonationUiState.PanelOpen = false;
    }

    private bool _wasOpen;

    private void Update()
    {
        if (Input.GetKeyDown(_toggleKey)) Toggle();

        // Free the cursor while open so the mouse can navigate the panel (see
        // DonationUiState for the matching input-suppression patches).
        if (_open)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible   = true;
            DonationUiState.SetMouseCapture(false);
        }
        else if (_wasOpen)
        {
            DonationUiState.SetMouseCapture(true);
        }

        if (_open != _wasOpen) { DonationUiState.PanelOpen = _open; _wasOpen = _open; }
    }

    private void Toggle()
    {
        _open = !_open;
        if (_open) RefreshStateSoon();
    }

    private void RefreshStateSoon()
    {
        if (Time.realtimeSinceStartup - _lastStateFetch < 1f) return;
        _lastStateFetch = Time.realtimeSinceStartup;
        StartCoroutine(FetchState());
    }

    private class StateResp
    {
        public int     balance;
        public TopEntry[] top_donors;
        public int     donor_count;
        public int     total_donated_coins;
    }

    private class TopEntry
    {
        public int    rank;
        public string name;
        public int    total_coins;
    }

    private IEnumerator FetchState()
    {
        var steam64 = ResolveLocalSteam64();
        if (string.IsNullOrEmpty(steam64)) yield break;

        yield return BackendClient.Get<StateResp>(
            $"/api/state/{steam64}?top=5",
            (ok, r, err) =>
            {
                if (!ok || r == null) return;
                _balance = r.balance;
                _topDonors = r.top_donors != null ? new List<TopEntry>(r.top_donors) : new List<TopEntry>();
            });
    }

    private string ResolveLocalSteam64()
    {
        try
        {
            // ZSteamMatchmaking has GetSteamID on most builds; fall back to ZPlatform.
            var t = Type.GetType("ZSteamMatchmaking, assembly_valheim");
            var inst = t?.GetField("instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null);
            var id = inst?.GetType().GetMethod("GetSteamID")?.Invoke(inst, null);
            return id?.ToString();
        }
        catch { return null; }
    }

    // ─── styling ──────────────────────────────────────────────────────────

    private void InitStyles()
    {
        _bg = new GUIStyle(GUI.skin.box);
        _bg.normal.background = SolidTex(new Color(0.08f, 0.07f, 0.05f, 0.97f));
        _bg.padding = new RectOffset(12, 12, 12, 12);

        _hdr = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold };
        _hdr.normal.textColor = new Color(0.85f, 0.7f, 0.4f);

        _btn = new GUIStyle(GUI.skin.button) { fontSize = 13 };
        _btn.padding = new RectOffset(10, 10, 6, 6);

        _btnActive = new GUIStyle(_btn);
        _btnActive.normal.background = SolidTex(new Color(0.6f, 0.45f, 0.2f, 1f));
        _btnActive.normal.textColor = Color.white;

        _line = new GUIStyle();
        _line.normal.background = SolidTex(new Color(0.3f, 0.25f, 0.18f, 0.6f));

        _logLine = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true };
        _logLine.normal.textColor = new Color(0.9f, 0.9f, 0.85f);

        _label = new GUIStyle(GUI.skin.label) { fontSize = 12 };
        _label.normal.textColor = new Color(0.88f, 0.85f, 0.78f);

        _stylesReady = true;
    }

    private static Texture2D SolidTex(Color c)
    {
        var t = new Texture2D(1, 1);
        t.SetPixel(0, 0, c);
        t.Apply();
        return t;
    }

    // ─── render ───────────────────────────────────────────────────────────

    private void OnGUI()
    {
        if (!_open) return;
        if (!_stylesReady) InitStyles();

        // Auto-close if the player opens a game menu.
        if (Menu.IsVisible() || (InventoryGui.instance != null && InventoryGui.IsVisible())
            || (Minimap.instance != null && Minimap.IsOpen()))
        {
            _open = false; return;
        }

        var rect = new Rect((Screen.width - PanelW) / 2f, (Screen.height - PanelH) / 2f, PanelW, PanelH);
        GUI.Box(rect, GUIContent.none, _bg);

        GUILayout.BeginArea(new Rect(rect.x + 12, rect.y + 12, rect.width - 24, rect.height - 24));

        // Header row.
        GUILayout.BeginHorizontal();
        GUILayout.Label("Valheim Donations", _hdr);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("✕", _btn, GUILayout.Width(30))) _open = false;
        GUILayout.EndHorizontal();

        GUILayout.Label($"Balance:  {_balance} Valcoins", _label);
        var perks = string.Join(", ", PerksForLocalPlayer());
        if (!string.IsNullOrEmpty(perks)) GUILayout.Label($"Perks: {perks}", _label);
        if (!Config.Ready)
            GUILayout.Label("🔌 Donation service offline — browse now; it activates when the operator connects it.", _label);

        DrawHr();

        // Tab strip.
        GUILayout.BeginHorizontal();
        TabButton("Donate", Tab.Donate);
        TabButton("Shop",   Tab.Shop);
        TabButton("Gift",   Tab.Gift);
        TabButton("Top",    Tab.Top);
        GUILayout.EndHorizontal();

        DrawHr();

        switch (_tab)
        {
            case Tab.Donate: DrawDonate(); break;
            case Tab.Shop:   DrawShop();   break;
            case Tab.Gift:   DrawGift();   break;
            case Tab.Top:    DrawTop();    break;
        }

        DrawHr();
        DrawLog();

        GUILayout.EndArea();
    }

    private void TabButton(string label, Tab t)
    {
        if (GUILayout.Button(label, _tab == t ? _btnActive : _btn, GUILayout.Height(28))) _tab = t;
    }

    private void DrawHr()
    {
        GUILayout.Box(GUIContent.none, _line, GUILayout.Height(1), GUILayout.ExpandWidth(true));
        GUILayout.Space(4);
    }

    private void DrawDonate()
    {
        GUILayout.Label("Get a donation code, then donate via the link.\nFunds turn into Valcoins automatically.", _label);
        GUILayout.Space(6);
        if (GUILayout.Button("🎁  Get my donation code", _btn, GUILayout.Height(34)))
            RpcLayer.SendAction("donate");
    }

    private Vector2 _shopScroll;
    private void DrawShop()
    {
        if (Catalog.Order.Count == 0)
        {
            GUILayout.Label("Shop is empty — ask the operator to set up valcoin_shop.yaml.", _label);
            return;
        }

        _shopScroll = GUILayout.BeginScrollView(_shopScroll, GUILayout.ExpandHeight(true));
        var steam64 = ResolveLocalSteam64();

        foreach (var sku in Catalog.Order)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{sku.Name}  —  {sku.Price}c", _label, GUILayout.ExpandWidth(true));

            bool owned = sku.Effect == "grant_perk" && !string.IsNullOrEmpty(steam64)
                          && PerkManager.Has(steam64, sku.Perk);
            int  charges = sku.Effect == "add_charges" && !string.IsNullOrEmpty(steam64)
                            ? PerkManager.Charges(steam64, sku.Perk) : 0;

            if (owned) GUILayout.Label("✓ owned", _label, GUILayout.Width(80));
            else if (charges > 0) GUILayout.Label($"x{charges} held", _label, GUILayout.Width(80));

            if (!owned)
            {
                if (GUILayout.Button("Buy", _btn, GUILayout.Width(70)))
                    RpcLayer.SendAction("buy:" + sku.Id);
            }
            GUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(sku.Description))
                GUILayout.Label("    " + sku.Description, _label);
            GUILayout.Space(4);
        }
        GUILayout.EndScrollView();
    }

    private void DrawGift()
    {
        GUILayout.Label("Send Valcoins to another player on the server.", _label);
        GUILayout.Space(4);
        GUILayout.BeginHorizontal();
        GUILayout.Label("To:", _label, GUILayout.Width(50));
        _giftTo = GUILayout.TextField(_giftTo ?? "", GUILayout.Width(200));
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        GUILayout.Label("Amount:", _label, GUILayout.Width(50));
        _giftAmount = GUILayout.TextField(_giftAmount ?? "", GUILayout.Width(80));
        GUILayout.EndHorizontal();

        GUILayout.Space(6);
        if (GUILayout.Button("🎁  Send gift", _btn, GUILayout.Height(28)))
        {
            if (string.IsNullOrWhiteSpace(_giftTo) || string.IsNullOrWhiteSpace(_giftAmount))
                AddLog("⚠️ Fill in both fields.");
            else
                RpcLayer.SendAction($"gift:{_giftTo.Trim()}:{_giftAmount.Trim()}");
        }

        // Title editor (only if perk owned).
        var steam64 = ResolveLocalSteam64();
        if (!string.IsNullOrEmpty(steam64) && PerkManager.Has(steam64, "chat_title"))
        {
            GUILayout.Space(10);
            GUILayout.Label("Chat title (clear with empty + Set):", _label);
            _titleText = GUILayout.TextField(_titleText ?? "", GUILayout.Width(200));
            if (GUILayout.Button("Set title", _btn, GUILayout.Width(110)))
                RpcLayer.SendAction("title:" + (string.IsNullOrWhiteSpace(_titleText) ? "clear" : _titleText.Trim()));
        }
    }

    private void DrawTop()
    {
        GUILayout.Label("Lifetime donor leaderboard:", _label);
        GUILayout.Space(4);
        if (_topDonors.Count == 0)
        {
            GUILayout.Label("(none yet — be the first!)", _label);
        }
        else
        {
            foreach (var e in _topDonors)
                GUILayout.Label($"  {e.rank}. {e.name ?? "Anonymous"}  —  {e.total_coins} coins", _label);
        }
        GUILayout.Space(6);
        if (GUILayout.Button("Refresh", _btn, GUILayout.Width(90)))
        {
            _topDonors.Clear();
            RefreshStateSoon();
        }
    }

    private void DrawLog()
    {
        if (_log.Count == 0) return;
        GUILayout.Label("Messages", _label);
        _logScroll = GUILayout.BeginScrollView(_logScroll, GUILayout.Height(80));
        for (int i = _log.Count - 1; i >= 0; i--)
            GUILayout.Label("• " + _log[i], _logLine);
        GUILayout.EndScrollView();
    }

    private IEnumerable<string> PerksForLocalPlayer()
    {
        var s = ResolveLocalSteam64();
        if (string.IsNullOrEmpty(s)) yield break;
        if (PerkManager.Has(s, "donor_badge"))     yield return "donor_badge";
        if (PerkManager.Has(s, "chat_title"))      yield return "chat_title";
        if (PerkManager.Has(s, "companion_flair")) yield return "companion_flair";
    }

    private void AddLog(string msg)
    {
        _log.Add(msg);
        if (_log.Count > LogCap) _log.RemoveAt(0);
        // An action almost always affects balance/state; cheaply refresh.
        RefreshStateSoon();
    }
}
