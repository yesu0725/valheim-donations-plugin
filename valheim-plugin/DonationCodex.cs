using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Client-side "Donation Codex" — the browsable, opt-in home for the donation
// system. Toggle with F4 (configurable via valcoin_config.json -> codex_toggle_key).
//
// Design intent (Phase 1):
//   * Fully navigable OFFLINE. Everything that can render from local data
//     (command reference, shop catalog, owned perks) shows immediately, with no
//     backend. Anything that needs the backend (balance, live patron board,
//     purchasing, donate codes) shows a friendly "activates when online" state
//     instead of an error.
//   * When the backend is reachable, those sections light up automatically —
//     no code change, the operator just fills in valcoin_config.json.
//
// Sections: Overview · Perks & Shop · Patrons · Donate
public class DonationCodex : MonoBehaviour
{
    private const int PanelW = 560;
    private const int PanelH = 520;

    private enum Section { Overview, Perks, Patrons, Donate }
    private Section _section = Section.Overview;
    private bool _open;

    private KeyCode _toggleKey = KeyCode.F4;

    // Online state: seeds from config, then flips true on any successful backend
    // call and false on a failed one — so the UI reflects reality live.
    private bool _online;
    private int  _balance = -1;                       // -1 = unknown/offline
    private List<TopEntry> _patrons = new List<TopEntry>();

    private float _lastFetch;
    private const float RefreshSeconds = 20f;

    private Vector2 _scroll;
    private GUIStyle _bg, _hdr, _sub, _btn, _btnActive, _btnDim, _line, _label, _pillOn, _pillOff;
    private bool _stylesReady;

    private class StateResp { public int balance; public TopEntry[] top_donors; }
    private class TopEntry  { public int rank; public string name; public int total_coins; }

    // ─── lifecycle ──────────────────────────────────────────────────────────

    private void Awake()
    {
        if (!string.IsNullOrEmpty(Config.CodexToggleKey)
            && Enum.TryParse<KeyCode>(Config.CodexToggleKey, true, out var parsed))
            _toggleKey = parsed;

        _online = Config.Ready;   // best guess until the first live call resolves
        DontDestroyOnLoad(gameObject);
    }

    private bool _wasOpen;

    private void Update()
    {
        if (Input.GetKeyDown(_toggleKey)) Toggle();

        // Free the cursor while open so the mouse can navigate the panel.
        // GameCamera re-captures the mouse on its own update, so we re-assert
        // every frame; m_mouseCapture = false makes the game itself stop
        // grabbing it. Restore capture on the frame we close.
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

        if (_open != _wasOpen) { DonationUiState.CodexOpen = _open; _wasOpen = _open; }
    }

    private void OnDestroy() => DonationUiState.CodexOpen = false;

    private void Toggle()
    {
        _open = !_open;
        if (_open) RefreshSoon(force: true);
    }

    private void RefreshSoon(bool force = false)
    {
        if (!Config.Ready) { _online = false; return; }
        if (!force && Time.realtimeSinceStartup - _lastFetch < RefreshSeconds) return;
        _lastFetch = Time.realtimeSinceStartup;
        StartCoroutine(Fetch());
    }

    private IEnumerator Fetch()
    {
        var steam64 = ResolveLocalSteam64();
        string path = string.IsNullOrEmpty(steam64)
            ? "/api/leaderboard/top?limit=5"
            : $"/api/state/{steam64}?top=5";

        yield return BackendClient.Get<StateResp>(path, (ok, r, err) =>
        {
            _online = ok && r != null;
            if (!_online) return;
            if (r.balance != 0 || !string.IsNullOrEmpty(steam64)) _balance = r.balance;
            _patrons = r.top_donors != null ? new List<TopEntry>(r.top_donors) : new List<TopEntry>();
        });
    }

    private string ResolveLocalSteam64()
    {
        try
        {
            var t = Type.GetType("ZSteamMatchmaking, assembly_valheim");
            var inst = t?.GetField("instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null);
            var id = inst?.GetType().GetMethod("GetSteamID")?.Invoke(inst, null);
            return id?.ToString();
        }
        catch { return null; }
    }

    // ─── render ───────────────────────────────────────────────────────────

    private void OnGUI()
    {
        if (!_open) return;
        if (!_stylesReady) InitStyles();

        if (Menu.IsVisible() || (InventoryGui.instance != null && InventoryGui.IsVisible())
            || (Minimap.instance != null && Minimap.IsOpen()))
        { _open = false; return; }

        var rect = new Rect((Screen.width - PanelW) / 2f, (Screen.height - PanelH) / 2f, PanelW, PanelH);
        GUI.Box(rect, GUIContent.none, _bg);
        GUILayout.BeginArea(new Rect(rect.x + 14, rect.y + 14, rect.width - 28, rect.height - 28));

        // Header: title + online/offline pill + close.
        GUILayout.BeginHorizontal();
        GUILayout.Label("Donation Codex", _hdr);
        GUILayout.FlexibleSpace();
        GUILayout.Label(_online ? "● Live" : "● Offline", _online ? _pillOn : _pillOff, GUILayout.Height(22));
        GUILayout.Space(6);
        if (GUILayout.Button("✕", _btn, GUILayout.Width(30))) _open = false;
        GUILayout.EndHorizontal();

        GUILayout.Label(_online
            ? $"Balance: {(_balance < 0 ? "—" : _balance.ToString())} Valcoins"
            : "The donation service isn't connected yet — everything here still works to browse; it activates once the operator brings it online.",
            _sub);

        DrawHr();

        GUILayout.BeginHorizontal();
        NavButton("Overview",     Section.Overview);
        NavButton("Perks & Shop", Section.Perks);
        NavButton("Patrons",      Section.Patrons);
        NavButton("Donate",       Section.Donate);
        GUILayout.EndHorizontal();

        DrawHr();

        _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
        switch (_section)
        {
            case Section.Overview: DrawOverview(); break;
            case Section.Perks:    DrawPerks();    break;
            case Section.Patrons:  DrawPatrons();  break;
            case Section.Donate:   DrawDonate();   break;
        }
        GUILayout.EndScrollView();

        GUILayout.EndArea();
    }

    private void NavButton(string label, Section s)
    {
        if (GUILayout.Button(label, _section == s ? _btnActive : _btn, GUILayout.Height(28))) _section = s;
    }

    private void DrawOverview()
    {
        GUILayout.Label("Support the realm", _hdr);
        GUILayout.Label(
            "This server is kept alight by its patrons. Donating is always optional — " +
            "playing is free, and perks are cosmetic or weekly-limited supplies, never power.",
            _label);
        GUILayout.Space(8);

        GUILayout.Label("How Valcoins work", _sub);
        Bullet("Open the Donate tab here to get a code + a portal link.");
        Bullet("Donate through the portal; Valcoins are credited automatically.");
        Bullet("Spend them in Perks & Shop; your balance is always shown above.");
        GUILayout.Space(8);

        GUILayout.Label("Everything's in the panels", _sub);
        Bullet("This Codex (F4) — browse the shop, patrons, and get your donation code.");
        Bullet($"The quick panel ({Config.UiToggleKey ?? "F8"}) — buy, gift, and check the leaderboard on the go.");
        Bullet("No chat commands needed — everything works from these two panels.");
        GUILayout.Space(8);
        GUILayout.Label($"Press {_toggleKey} any time to open this Codex.", _label);
    }

    private void DrawPerks()
    {
        if (Catalog.Order.Count == 0)
        {
            GUILayout.Label("The shop is empty — the operator hasn't set up valcoin_shop.yaml yet.", _label);
            return;
        }

        var steam64 = ResolveLocalSteam64();
        foreach (var sku in Catalog.Order)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{sku.Name}  ·  {sku.Price}c", _label, GUILayout.ExpandWidth(true));

            bool owned = sku.Effect == "grant_perk" && !string.IsNullOrEmpty(steam64)
                          && PerkManager.Has(steam64, sku.Perk);

            if (owned)
            {
                GUILayout.Label("✓ owned", _label, GUILayout.Width(90));
            }
            else if (_online)
            {
                if (GUILayout.Button("Buy", _btn, GUILayout.Width(70)))
                    RpcLayer.SendAction("buy:" + sku.Id);
            }
            else
            {
                GUILayout.Label("Buy", _btnDim, GUILayout.Width(70));
            }
            GUILayout.EndHorizontal();

            // Consumable metadata (weekly cap + boss gate) or description.
            if (sku.Effect == "grant_item")
            {
                string meta = "";
                if (sku.WeeklyCap > 0) meta += $"max {sku.WeeklyCap}/week";
                if (!string.IsNullOrEmpty(sku.RequiresBoss))
                    meta += (meta.Length > 0 ? " · " : "") + $"needs {sku.RequiresBoss}";
                if (meta.Length > 0) GUILayout.Label("    " + meta, _label);
            }
            if (!string.IsNullOrEmpty(sku.Description))
                GUILayout.Label("    " + sku.Description, _label);
            GUILayout.Space(6);
        }

        if (!_online)
        {
            DrawHr();
            GUILayout.Label("Purchasing activates once the donation service is online. " +
                            "You can browse the full catalog now.", _sub);
        }
    }

    private void DrawPatrons()
    {
        GUILayout.Label("Top Patrons", _hdr);
        GUILayout.Space(4);

        if (!_online)
        {
            GUILayout.Label("The patron leaderboard appears here once the donation service is online.", _label);
            return;
        }
        if (_patrons.Count == 0)
        {
            GUILayout.Label("No patrons yet — be the first. Head to the Donate tab!", _label);
            return;
        }
        foreach (var e in _patrons)
            GUILayout.Label($"  {e.rank}.  {e.name ?? "Anonymous"}  —  {e.total_coins} coins", _label);

        GUILayout.Space(8);
        if (GUILayout.Button("Refresh", _btn, GUILayout.Width(90)))
        { _patrons.Clear(); RefreshSoon(force: true); }
    }

    private void DrawDonate()
    {
        GUILayout.Label("Make a donation", _hdr);
        GUILayout.Label("Get a personal code, donate through the portal link, and your " +
                        "Valcoins are credited automatically within a few seconds.", _label);
        GUILayout.Space(8);

        if (_online)
        {
            if (GUILayout.Button("🎁  Get my donation code", _btn, GUILayout.Height(34)))
                RpcLayer.SendAction("donate");
            GUILayout.Space(4);
            GUILayout.Label("Your code + link will appear in the top-left message area.", _label);
        }
        else
        {
            GUILayout.Label("Donations aren't connected yet.", _sub);
            GUILayout.Label("Once the operator brings the donation service online, this button " +
                            "will hand you a code and a link — no update needed on your side.", _label);
        }
    }

    private void Bullet(string text) => GUILayout.Label("•  " + text, _label);

    private void DrawHr()
    {
        GUILayout.Box(GUIContent.none, _line, GUILayout.Height(1), GUILayout.ExpandWidth(true));
        GUILayout.Space(4);
    }

    // ─── styling ──────────────────────────────────────────────────────────

    private void InitStyles()
    {
        _bg = new GUIStyle(GUI.skin.box);
        _bg.normal.background = SolidTex(new Color(0.08f, 0.07f, 0.05f, 0.97f));
        _bg.padding = new RectOffset(12, 12, 12, 12);

        _hdr = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold };
        _hdr.normal.textColor = new Color(0.85f, 0.7f, 0.4f);

        _sub = new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Italic, wordWrap = true };
        _sub.normal.textColor = new Color(0.75f, 0.72f, 0.62f);

        _btn = new GUIStyle(GUI.skin.button) { fontSize = 13 };
        _btn.padding = new RectOffset(10, 10, 6, 6);

        _btnActive = new GUIStyle(_btn);
        _btnActive.normal.background = SolidTex(new Color(0.6f, 0.45f, 0.2f, 1f));
        _btnActive.normal.textColor = Color.white;

        _btnDim = new GUIStyle(_btn);
        _btnDim.normal.textColor = new Color(0.5f, 0.48f, 0.42f);

        _line = new GUIStyle();
        _line.normal.background = SolidTex(new Color(0.3f, 0.25f, 0.18f, 0.6f));

        _label = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true };
        _label.normal.textColor = new Color(0.88f, 0.85f, 0.78f);

        _pillOn = new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold };
        _pillOn.normal.textColor = new Color(0.5f, 0.85f, 0.45f);

        _pillOff = new GUIStyle(_pillOn);
        _pillOff.normal.textColor = new Color(0.85f, 0.6f, 0.3f);

        _stylesReady = true;
    }

    private static Texture2D SolidTex(Color c)
    {
        var t = new Texture2D(1, 1);
        t.SetPixel(0, 0, c);
        t.Apply();
        return t;
    }
}
