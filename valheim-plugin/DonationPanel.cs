using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

// Client-side in-game GUI for the donations system — the single, combined
// panel (this replaced the earlier split F4 "Codex" + F8 "quick panel").
//
// Opens with the F4 key (configurable via valcoin_config.json ->
// codex_toggle_key). There is a single hotkey by design.
//
// Design goals:
//   * Zero external UI dependencies (no Jotunn). Unity IMGUI only.
//   * No emoji anywhere — Valheim's IMGUI font renders them as blank squares,
//     so everything here is plain ASCII, with colour/weight for emphasis.
//   * Doesn't block gameplay — closes if any modal/menu opens.
//   * Sends silent RPCs instead of typing into public chat.
//
// Tabs: Donate | Shop | Gift | Patrons | (Admin, admins only)
public class DonationPanel : MonoBehaviour
{
    private const int PanelW = 640;
    private const int PanelH = 760;   // desired height; clamped to the screen in OnGUI

    private enum Tab { Donate, Shop, Gift, Patrons, Admin }
    private Tab _tab = Tab.Donate;
    private bool _open;

    // Set from the server's reply to a "whoami" action (see UiActionRouter);
    // the client can't know the local Steam64 is an admin on its own, since
    // that list only lives server-side.
    private bool _isAdmin;
    private bool _askedWhoAmI;

    private KeyCode _toggleKey = KeyCode.F4;    // codex_toggle_key

    // Cached per-player state.
    private int _balance;
    private List<TopEntry> _topDonors = new List<TopEntry>();

    // Shop state fetched from the backend (the client can't read server-side
    // PerkManager or the backend spend ledger directly, so /api/state hands it
    // over): SKUs already bought, purchases used this week, and when the weekly
    // counters reset. Drives the "Already Purchased" / weekly-cap UI.
    private HashSet<string> _ownedSkus = new HashSet<string>();
    private Dictionary<string, int> _weeklyUsage = new Dictionary<string, int>();
    private string _weekResetsIn = "";
    private Dictionary<string, int> _charges = new Dictionary<string, int>();

    // Authoritative Valcoins-per-USD rate from the backend (0 until first state
    // fetch), shown as an exchange-rate note on the Donate and Shop tabs.
    private float _coinsPerUsd;

    // Donate tab state.
    private string _donateCode;                  // null until a code arrives
    private string _donateUrl;
    private int    _donateTtlMinutes;
    private string _donateStatus;                // transient error/status line
    private float  _donateCooldownUntil;         // realtimeSinceStartup
    private float  _copiedFlashUntil;            // "Copied!" transient
    private const float DonateCooldownSeconds = 30f;

    // Terms-of-use modal.
    private bool _showTerms;
    private Vector2 _termsScroll;

    // Purchase-confirm modal — the SKU awaiting a Yes/Cancel decision. The Buy
    // button only stages the SKU here; the spend RPC fires on "Yes".
    private Catalog.Sku _confirmSku;

    // Image-zoom overlay: the preview source currently shown full-size, plus the
    // SKU name to caption it with. Set by clicking any preview thumbnail; drawn
    // above every other modal so it can be opened from the confirm dialog and
    // dismissed back to it.
    private string _zoomImage;
    private string _zoomCaption;

    // Purchase-result modal — after "Yes" we arm a pending purchase; the next
    // plain server reply becomes a success/failure modal (with a timeout
    // fallback if the server never answers, e.g. connection dropped mid-buy).
    private string _pendingBuySku;
    private float  _pendingBuyDeadline;
    private string _resultText;      // non-null => result modal is up
    private bool   _resultSuccess;
    private string _resultExtra;     // e.g. armor-effect apply outcome

    // Buffer for server-pushed messages (buy/gift/admin results).
    private readonly List<string> _log = new List<string>();
    private const int LogCap = 12;
    private Vector2 _logScroll;

    // Gift / title text fields.
    private string _giftTo = "", _giftAmount = "";
    private string _adminTarget = "", _adminAmount = "";

    private GUIStyle _bg, _hdr, _sub, _btn, _btnActive, _btnDim, _btnPrimary,
                     _line, _logLine, _label, _codeBox, _linkBtn, _pillOn, _pillOff,
                     _owned, _catHdr, _dim, _rateBox, _rateSub;
    private bool _stylesReady;

    private float _lastStateFetch;
    private const float AutoRefreshSeconds = 20f;
    private bool _online;

    // ─── lifecycle ────────────────────────────────────────────────────────

    private void Awake()
    {
        if (!string.IsNullOrEmpty(Config.CodexToggleKey)
            && Enum.TryParse<KeyCode>(Config.CodexToggleKey, true, out var k))
            _toggleKey = k;

        RpcLayer.OnPanelMessage += OnServerMessage;
        DontDestroyOnLoad(gameObject);
    }

    private void OnDestroy()
    {
        RpcLayer.OnPanelMessage -= OnServerMessage;
        DonationUiState.PanelOpen = false;
    }

    private bool _wasOpen;

    private void Update()
    {
        if (Input.GetKeyDown(_toggleKey)) Toggle();

        if (_open)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible   = true;
            DonationUiState.SetMouseCapture(false);
            RefreshStateSoon();
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
        if (_open)
        {
            RefreshStateSoon(force: true);
            if (!_askedWhoAmI) { RpcLayer.SendAction("whoami"); _askedWhoAmI = true; }
        }
    }

    private void RefreshStateSoon(bool force = false)
    {
        if (!Config.Ready) { _online = false; return; }
        float threshold = force ? 1f : AutoRefreshSeconds;
        if (Time.realtimeSinceStartup - _lastStateFetch < threshold) return;
        _lastStateFetch = Time.realtimeSinceStartup;
        StartCoroutine(FetchState());
    }

    private class StateResp
    {
        public int balance;
        public TopEntry[] top_donors;
        public string[] owned_skus;
        public Dictionary<string, int> weekly_usage;
        public string week_resets_in;
        public Dictionary<string, int> charges;
        public float coins_per_usd;
    }

    private class TopEntry
    {
        public int rank;
        public string name;
        public int total_coins;
    }

    private IEnumerator FetchState()
    {
        var steam64 = ResolveLocalSteam64();
        if (string.IsNullOrEmpty(steam64))
        {
            _online = false;
            Debug.LogWarning("[Valcoin] Panel offline: couldn't resolve local Steam ID yet.");
            yield break;
        }

        yield return BackendClient.Get<StateResp>(
            $"/api/state/{steam64}?top=5",
            (ok, r, err) =>
            {
                _online = ok && r != null;
                if (!_online)
                {
                    Debug.LogWarning($"[Valcoin] Panel offline: /api/state failed ({err ?? "no response"}).");
                    return;
                }
                _balance = r.balance;
                _topDonors = r.top_donors != null ? new List<TopEntry>(r.top_donors) : new List<TopEntry>();
                _ownedSkus = r.owned_skus != null ? new HashSet<string>(r.owned_skus) : new HashSet<string>();
                _weeklyUsage = r.weekly_usage ?? new Dictionary<string, int>();
                _weekResetsIn = r.week_resets_in ?? "";
                _charges = r.charges ?? new Dictionary<string, int>();
                _coinsPerUsd = r.coins_per_usd;
                _charges.TryGetValue(SoulkeeperState.Kind, out var sk);
                SoulkeeperState.UpdateFromState(steam64, sk);
            });
    }

    // Resolve THIS client's own Steam64 (shared resolver — see LocalIdentity).
    private string ResolveLocalSteam64() => LocalIdentity.Steam64();

    // ─── server messages ────────────────────────────────────────────────────

    private const string AdminStatusPrefix = "__ADMIN__:";
    private const string DonateOkPrefix     = "__DONATE__:";
    private const string DonateErrPrefix    = "__DONATE_ERR__:";
    private const string ArmorVfxPrefix     = "__ARMORVFX__:";

    private void OnServerMessage(string msg)
    {
        if (msg == null) return;

        if (msg.StartsWith(AdminStatusPrefix))
        {
            _isAdmin = msg.Substring(AdminStatusPrefix.Length) == "true";
            return;
        }

        if (msg.StartsWith(DonateOkPrefix))
        {
            // Format: code|url|ttlMinutes
            var body = msg.Substring(DonateOkPrefix.Length);
            var parts = body.Split(new[] { '|' }, 3);
            _donateCode = parts.Length > 0 ? parts[0] : null;
            _donateUrl  = parts.Length > 1 ? parts[1] : null;
            _donateTtlMinutes = (parts.Length > 2 && int.TryParse(parts[2], out var t)) ? t : 0;
            _donateStatus = null;
            return;
        }

        if (msg.StartsWith(DonateErrPrefix))
        {
            _donateStatus = msg.Substring(DonateErrPrefix.Length);
            _donateCooldownUntil = 0f;   // let them retry immediately after a failure
            return;
        }

        if (msg.StartsWith(ArmorVfxPrefix))
        {
            // Format: <aura>:<slot>. The spend already succeeded server-side;
            // apply the cosmetic + rename on this (the buyer's) client, and log
            // whatever it reports (success line or "equip a piece first").
            var body = msg.Substring(ArmorVfxPrefix.Length).Split(new[] { ':' }, 2);
            string m;
            if (body.Length == 2) ArmorVfx.ApplyToEquipped(body[0], body[1], out m);
            else m = "Armor effect could not be applied.";
            _log.Add(m);
            if (_log.Count > LogCap) _log.RemoveAt(0);
            // Fold the apply outcome into the pending purchase-result modal.
            if (_pendingBuySku != null) _resultExtra = m;
            return;
        }

        // Everything else is a buy/gift/admin result line.
        _log.Add(msg);
        if (_log.Count > LogCap) _log.RemoveAt(0);

        // If a purchase is awaiting its outcome, this reply is it — surface it
        // as a modal (success = the known "it worked" phrasings from ShopHandler).
        if (_pendingBuySku != null)
        {
            _pendingBuySku = null;
            _resultSuccess = msg.StartsWith("Purchased") || msg.Contains("was already processed");
            _resultText = string.IsNullOrEmpty(_resultExtra) ? msg : msg + "\n\n" + _resultExtra;
            _resultExtra = null;
        }
        RefreshStateSoon();
    }

    // ─── styling ──────────────────────────────────────────────────────────

    private void InitStyles()
    {
        // Dark-wood panel with a bronze frame (Valheim-style).
        _bg = new GUIStyle(GUI.skin.box);
        _bg.normal.background = BorderTex(new Color(0.09f, 0.08f, 0.06f, 0.985f),
                                          new Color(0.42f, 0.32f, 0.16f, 1f), 2);
        _bg.border = new RectOffset(3, 3, 3, 3);
        _bg.padding = new RectOffset(14, 14, 14, 14);

        _hdr = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold };
        _hdr.normal.textColor = new Color(0.87f, 0.72f, 0.42f);

        _sub = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Italic, wordWrap = true };
        _sub.normal.textColor = new Color(0.75f, 0.72f, 0.62f);

        // Dark bronze-trimmed button; brightens on hover.
        _btn = new GUIStyle(GUI.skin.button) { fontSize = 15 };
        _btn.border = new RectOffset(3, 3, 3, 3);
        _btn.padding = new RectOffset(10, 10, 7, 7);
        _btn.normal.background = BorderTex(new Color(0.17f, 0.14f, 0.10f, 1f),
                                          new Color(0.46f, 0.36f, 0.19f, 1f), 2);
        _btn.hover.background  = BorderTex(new Color(0.26f, 0.21f, 0.13f, 1f),
                                          new Color(0.68f, 0.53f, 0.27f, 1f), 2);
        _btn.active.background = _btn.hover.background;
        _btn.normal.textColor = new Color(0.92f, 0.86f, 0.72f);
        _btn.hover.textColor  = new Color(1f, 0.96f, 0.86f);

        // Selected tab: filled bronze.
        _btnActive = new GUIStyle(_btn);
        _btnActive.normal.background = BorderTex(new Color(0.5f, 0.38f, 0.18f, 1f),
                                                new Color(0.72f, 0.57f, 0.29f, 1f), 2);
        _btnActive.hover.background = _btnActive.normal.background;
        _btnActive.normal.textColor = new Color(1f, 0.97f, 0.88f);
        _btnActive.hover.textColor  = Color.white;

        // Disabled/inert button (owned/locked/capped states).
        _btnDim = new GUIStyle(_btn);
        _btnDim.normal.background = BorderTex(new Color(0.13f, 0.12f, 0.10f, 1f),
                                             new Color(0.30f, 0.26f, 0.18f, 1f), 2);
        _btnDim.hover.background = _btnDim.normal.background;
        _btnDim.normal.textColor = new Color(0.5f, 0.48f, 0.42f);
        _btnDim.hover.textColor  = new Color(0.5f, 0.48f, 0.42f);

        // Prominent, high-contrast primary action (the donate button).
        // Vertical padding kept modest so the label sits centered at a normal
        // button height instead of floating in an oversized gold slab.
        _btnPrimary = new GUIStyle(GUI.skin.button) { fontSize = 16, fontStyle = FontStyle.Bold };
        _btnPrimary.alignment = TextAnchor.MiddleCenter;
        _btnPrimary.border = new RectOffset(3, 3, 3, 3);
        _btnPrimary.padding = new RectOffset(12, 12, 6, 6);
        _btnPrimary.normal.background = BorderTex(new Color(0.78f, 0.6f, 0.22f, 1f),
                                                 new Color(0.5f, 0.36f, 0.12f, 1f), 2);
        _btnPrimary.normal.textColor = new Color(0.12f, 0.08f, 0.02f);   // near-black on gold, high contrast
        _btnPrimary.hover.background = BorderTex(new Color(0.9f, 0.71f, 0.28f, 1f),
                                                new Color(0.6f, 0.44f, 0.16f, 1f), 2);
        _btnPrimary.hover.textColor = Color.black;
        _btnPrimary.active.background = _btnPrimary.normal.background;

        _line = new GUIStyle();
        _line.normal.background = SolidTex(new Color(0.3f, 0.25f, 0.18f, 0.6f));

        _logLine = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = true };
        _logLine.normal.textColor = new Color(0.9f, 0.9f, 0.85f);

        _label = new GUIStyle(GUI.skin.label) { fontSize = 15, wordWrap = true };
        _label.normal.textColor = new Color(0.88f, 0.85f, 0.78f);

        // Green "Already Purchased" marker for owned one-time perks.
        _owned = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight };
        _owned.normal.textColor = new Color(0.5f, 0.85f, 0.45f);

        // The donation code, shown big and bright in a dark box.
        _codeBox = new GUIStyle(GUI.skin.box) { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        _codeBox.normal.background = SolidTex(new Color(0.05f, 0.05f, 0.04f, 1f));
        _codeBox.normal.textColor = new Color(1f, 0.86f, 0.45f);
        _codeBox.padding = new RectOffset(8, 8, 10, 10);

        // "Terms of Use" rendered as a link.
        _linkBtn = new GUIStyle(GUI.skin.label) { fontSize = 14 };
        _linkBtn.normal.textColor = new Color(0.55f, 0.75f, 0.95f);
        _linkBtn.hover.textColor = new Color(0.75f, 0.88f, 1f);

        _pillOn = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight };
        _pillOn.normal.textColor = new Color(0.5f, 0.85f, 0.45f);

        _pillOff = new GUIStyle(_pillOn);
        _pillOff.normal.textColor = new Color(0.85f, 0.6f, 0.3f);

        // Shop category header — gold, bold, all-caps look (caller upper-cases).
        _catHdr = new GUIStyle(GUI.skin.label) { fontSize = 17, fontStyle = FontStyle.Bold };
        _catHdr.normal.textColor = new Color(0.85f, 0.68f, 0.34f);

        // Dim secondary line for auto-derived bundle contents.
        _dim = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = true };
        _dim.normal.textColor = new Color(0.62f, 0.60f, 0.53f);

        // Exchange-rate callout on the Donate tab — the one number donors most
        // want before they open their wallet, so it gets a bright gold framed
        // box rather than the usual muted helper-text treatment.
        _rateBox = new GUIStyle(GUI.skin.box) { fontSize = 24, fontStyle = FontStyle.Bold,
                                                alignment = TextAnchor.MiddleCenter, wordWrap = false };
        _rateBox.normal.background = BorderTex(new Color(0.16f, 0.13f, 0.07f, 1f),
                                               new Color(0.72f, 0.56f, 0.24f, 1f), 2);
        _rateBox.normal.textColor = new Color(1f, 0.86f, 0.45f);
        _rateBox.border = new RectOffset(3, 3, 3, 3);
        _rateBox.padding = new RectOffset(10, 10, 12, 6);

        // Caption under the callout (inside the same visual block).
        _rateSub = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleCenter, wordWrap = true };
        _rateSub.normal.textColor = new Color(0.75f, 0.72f, 0.62f);

        // Swap every style over to Valheim's own font (if we found it) so the
        // panel reads as part of the game. Serif metrics are a touch taller than
        // the IMGUI default, so line heights get a hair more room — sizes above
        // were chosen to stay clear of clipping either way.
        var font = GameFont();
        if (font != null)
            foreach (var s in new[] { _hdr, _sub, _btn, _btnActive, _btnDim, _btnPrimary,
                                      _logLine, _label, _codeBox, _linkBtn, _pillOn, _pillOff,
                                      _owned, _catHdr, _dim, _rateBox, _rateSub })
                s.font = font;

        _stylesReady = true;
    }

    private static Texture2D SolidTex(Color c)
    {
        var t = new Texture2D(1, 1);
        t.SetPixel(0, 0, c);
        t.Apply();
        return t;
    }

    // A 9-slice-able panel/button texture: a solid fill with a `t`-pixel border,
    // used with GUIStyle.border so the frame stays crisp at any size. Gives the
    // IMGUI panel Valheim's dark-wood-with-bronze-trim look without needing the
    // game's own UI sprites (which aren't reachable without Jotunn).
    private static Texture2D BorderTex(Color fill, Color border, int t = 2, int size = 16)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var px = new Color[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                px[y * size + x] = (x < t || y < t || x >= size - t || y >= size - t) ? border : fill;
        tex.SetPixels(px);
        tex.Apply();
        tex.filterMode = FilterMode.Point;
        tex.wrapMode = TextureWrapMode.Clamp;
        return tex;
    }

    // Valheim's own UI font, so our text matches the game. Found among the
    // loaded fonts by name (AveriaSerifLibre is the body serif; Norse the
    // runic display face). Falls back to the IMGUI default if neither is
    // present, so a font-name change in a future Valheim build just reverts to
    // plain text rather than breaking.
    private static Font _gameFont;
    private static bool _gameFontSearched;
    private static Font GameFont()
    {
        if (_gameFontSearched) return _gameFont;
        _gameFontSearched = true;
        try
        {
            var fonts = Resources.FindObjectsOfTypeAll<Font>();
            // Prefer the regular serif for body text (headers synthesize bold via
            // fontStyle); fall back through the runic display faces.
            string[] prefs = { "AveriaSerifLibre-Regular", "AveriaSerifLibre", "Averia", "Norse" };
            foreach (var pref in prefs)
            {
                foreach (var f in fonts)
                    if (f != null && !string.IsNullOrEmpty(f.name)
                        && f.name.IndexOf(pref, StringComparison.OrdinalIgnoreCase) >= 0)
                    { _gameFont = f; break; }
                if (_gameFont != null) break;
            }
            Debug.Log($"[Valcoin] UI font: {(_gameFont != null ? _gameFont.name : "default (Valheim font not found)")}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Valcoin] Font lookup failed: {ex.Message}");
        }
        return _gameFont;
    }

    // ─── render ───────────────────────────────────────────────────────────

    private void OnGUI()
    {
        if (!_open) return;
        if (!_stylesReady) InitStyles();

        if (Menu.IsVisible() || (InventoryGui.instance != null && InventoryGui.IsVisible())
            || (Minimap.instance != null && Minimap.IsOpen()))
        {
            _open = false; return;
        }

        // Clamp to the screen so a tall panel is never drawn off the top/bottom
        // edge (which would hide the lower tabs' content).
        float pw = Mathf.Min(PanelW, Screen.width - 40);
        float ph = Mathf.Min(PanelH, Screen.height - 40);
        var rect = new Rect((Screen.width - pw) / 2f, (Screen.height - ph) / 2f, pw, ph);
        GUI.Box(rect, GUIContent.none, _bg);

        // Pending purchase that never got a reply → failure modal via timeout.
        if (_pendingBuySku != null && Time.realtimeSinceStartup > _pendingBuyDeadline)
        {
            _pendingBuySku = null;
            _resultSuccess = false;
            _resultText = "No response from the server. Check your balance and the "
                          + "message log before retrying - the purchase may still have gone through.";
        }

        // While a modal is up, disable + dim the panel behind it so its buttons
        // can't be clicked through the overlay (IMGUI renders disabled controls
        // greyed, which reads as a native modal backdrop).
        bool modalOpen = _showTerms || _confirmSku != null || _resultText != null || _zoomImage != null;
        GUI.enabled = !modalOpen;

        GUILayout.BeginArea(new Rect(rect.x + 14, rect.y + 14, rect.width - 28, rect.height - 28));

        // Header row: title + live/offline + close.
        GUILayout.BeginHorizontal();
        GUILayout.Label("Valheim Donations", _hdr);
        GUILayout.FlexibleSpace();
        GUILayout.Label(_online ? "Live" : "Offline", _online ? _pillOn : _pillOff, GUILayout.Width(70), GUILayout.Height(22));
        GUILayout.Space(6);
        if (GUILayout.Button("X", _btn, GUILayout.Width(30))) _open = false;
        GUILayout.EndHorizontal();

        GUILayout.Label($"Balance:  {_balance} Valcoins", _label);
        DrawOwnedCharges();
        if (!_online)
            GUILayout.Label(Config.Ready
                ? "Can't reach the donation service right now - you can still browse; it reconnects automatically."
                : "This client isn't configured yet (ask the operator) - you can still browse.",
                _sub);

        DrawHr();

        // Tab strip.
        GUILayout.BeginHorizontal();
        TabButton("Donate",  Tab.Donate);
        TabButton("Shop",    Tab.Shop);
        TabButton("Gift",    Tab.Gift);
        TabButton("Patrons", Tab.Patrons);
        if (_isAdmin) TabButton("Admin", Tab.Admin);
        GUILayout.EndHorizontal();

        DrawHr();

        switch (_tab)
        {
            case Tab.Donate:  DrawDonate();  break;
            case Tab.Shop:    DrawShop();    break;
            case Tab.Gift:    DrawGift();    break;
            case Tab.Patrons: DrawPatrons(); break;
            case Tab.Admin:   DrawAdmin();   break;
        }

        // Message log (buy/gift/admin results) — not shown on the Donate tab,
        // which has its own inline feedback.
        if (_tab != Tab.Donate && _log.Count > 0)
        {
            DrawHr();
            DrawLog();
        }

        GUILayout.EndArea();

        // Modals draw last, at full opacity, so they sit above the dimmed panel.
        // Result outranks confirm (a result can only exist after a confirm).
        // Zoom outranks everything: it can be opened from the confirm dialog, and
        // closing it must fall back to whatever was underneath.
        GUI.enabled = true;
        if (_zoomImage != null) DrawZoomModal();
        else if (_showTerms) DrawTermsModal(rect);
        else if (_resultText != null) DrawResultModal();
        else if (_confirmSku != null) DrawConfirmModal();
    }

    // ─── Purchase result modal ──────────────────────────────────────────────

    private void DrawResultModal()
    {
        GUI.Box(new Rect(0, 0, Screen.width, Screen.height), GUIContent.none, _line);

        int w = Mathf.Min(460, Screen.width - 60);
        int h = Mathf.Min(260, Screen.height - 60);
        var r = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
        GUI.Box(r, GUIContent.none, _bg);

        GUILayout.BeginArea(new Rect(r.x + 18, r.y + 18, r.width - 36, r.height - 36));

        // Green title on success, ember-orange on failure (matches the Live/
        // Offline pill colors used in the header).
        var prev = GUI.contentColor;
        GUI.contentColor = _resultSuccess ? new Color(0.5f, 0.85f, 0.45f) : new Color(0.95f, 0.55f, 0.3f);
        GUILayout.Label(_resultSuccess ? "Purchase Complete" : "Purchase Failed", _hdr);
        GUI.contentColor = prev;

        DrawHr();
        GUILayout.Space(8);
        GUILayout.Label(_resultText, _label);

        GUILayout.FlexibleSpace();
        if (GUILayout.Button("OK", _btnPrimary, GUILayout.Height(38)))
            _resultText = null;

        GUILayout.EndArea();
    }

    // Persistent summary of owned consumable charges, shown under the balance
    // on every tab (not just the Shop, which the player might not open again
    // after buying — e.g. to check "do I still have a Soulkeeper charge?"
    // before heading into a fight).
    private void DrawOwnedCharges()
    {
        bool any = false;
        foreach (var kv in _charges)
        {
            if (kv.Value <= 0) continue;
            if (!any) { GUILayout.BeginHorizontal(); any = true; GUILayout.Label("Charges:", _label, GUILayout.Width(70)); }
            GUILayout.Label($"{ChargeLabel(kv.Key)} x{kv.Value}", _pillOn, GUILayout.ExpandWidth(false));
            GUILayout.Space(10);
        }
        if (any) { GUILayout.FlexibleSpace(); GUILayout.EndHorizontal(); }
    }

    private static readonly Regex TierSuffix = new Regex(@"\s*\(x\d+\)\s*$");

    // Charges are keyed by SKU.Perk (e.g. "soulkeeper"), not a display name.
    // Derive one from any catalog SKU that grants this charge kind, stripping
    // the per-tier "(xN)" suffix so "Soulkeeper Charm (x10)" -> "Soulkeeper Charm".
    private string ChargeLabel(string kind)
    {
        foreach (var sku in Catalog.Items.Values)
            if (sku.Effect == "add_charges" && sku.Perk == kind)
                return TierSuffix.Replace(sku.Name, "").Trim();
        return kind;
    }

    private void TabButton(string label, Tab t)
    {
        if (GUILayout.Button(label, _tab == t ? _btnActive : _btn, GUILayout.Height(32))) _tab = t;
    }

    private void DrawHr()
    {
        GUILayout.Box(GUIContent.none, _line, GUILayout.Height(1), GUILayout.ExpandWidth(true));
        GUILayout.Space(4);
    }

    // ─── Donate tab ─────────────────────────────────────────────────────────

    private void DrawDonate()
    {
        GUILayout.Label("Support the server", _hdr);
        GUILayout.Label(
            "Donating is always optional. Playing is free, and every perk is cosmetic " +
            "or a weekly-limited supply - never raw power.", _sub);
        GUILayout.Space(8);
        DrawRateCallout();

        GUILayout.Label("How it works", _label);
        GUILayout.Label("1.  Click \"Get my donation code\" below to generate your personal code.", _label);
        GUILayout.Label("2.  Click \"Open donation portal\" - it opens in your web browser.", _label);
        GUILayout.Label("3.  Pick a provider (Ko-fi, PayPal, Patreon, or GCash/Maya).", _label);
        GUILayout.Label("4.  Paste your code into the donation message if it isn't already filled in.", _label);
        GUILayout.Label("5.  Your Valcoins are credited automatically within a few seconds.", _label);
        GUILayout.Space(10);

        if (!_online)
        {
            GUILayout.Label("Donations aren't connected yet. Once the operator brings the service " +
                            "online, this button will hand you a code and a portal link.", _sub);
            return;
        }

        // The primary action, with a spam cooldown.
        float now = Time.realtimeSinceStartup;
        bool onCooldown = now < _donateCooldownUntil;

        if (onCooldown)
        {
            int wait = Mathf.CeilToInt(_donateCooldownUntil - now);
            GUILayout.Label($"Please wait {wait}s before requesting another code", _btnDim, GUILayout.Height(38));
        }
        else if (GUILayout.Button("Get my donation code", _btnPrimary, GUILayout.Height(38)))
        {
            _donateCooldownUntil = now + DonateCooldownSeconds;
            _donateStatus = "Requesting your code...";
            _donateCode = null;
            RpcLayer.SendAction("donate");
        }

        // Transient status / error line.
        if (!string.IsNullOrEmpty(_donateStatus))
        {
            GUILayout.Space(4);
            GUILayout.Label(_donateStatus, _sub);
        }

        // The code + actions appear right here once it arrives.
        if (!string.IsNullOrEmpty(_donateCode))
        {
            GUILayout.Space(8);
            GUILayout.Label("Your donation code:", _label);
            GUILayout.Box(_donateCode, _codeBox, GUILayout.Height(46), GUILayout.ExpandWidth(true));

            GUILayout.Space(6);
            // Stacked full-width buttons, matched heights so they read as a
            // balanced pair (Copy = secondary, Open = gold primary).
            if (GUILayout.Button("Copy code", _btn, GUILayout.Height(36)))
            {
                GUIUtility.systemCopyBuffer = _donateCode;
                _copiedFlashUntil = now + 2f;
            }
            if (!string.IsNullOrEmpty(_donateUrl))
            {
                GUILayout.Space(4);
                if (GUILayout.Button("Open donation portal", _btnPrimary, GUILayout.Height(36)))
                    Application.OpenURL(_donateUrl);
            }

            if (now < _copiedFlashUntil)
                GUILayout.Label("Copied to clipboard!", _sub);
            if (_donateTtlMinutes > 0)
                GUILayout.Label($"This code expires in about {_donateTtlMinutes} minutes.", _sub);
        }

        GUILayout.Space(12);
        DrawHr();
        GUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Terms of Use", _linkBtn)) _showTerms = true;
        GUILayout.EndHorizontal();
    }

    // Big gold "$1 USD = N Valcoins" callout for the Donate tab. The rate is
    // backend-supplied; if the service is reachable but didn't report one (an
    // older backend that predates coins_per_usd), say so explicitly rather than
    // rendering nothing — a silently missing rate is indistinguishable from a
    // bug, and this is the number donors look for first.
    private void DrawRateCallout()
    {
        if (_coinsPerUsd > 0f)
        {
            GUILayout.Box($"$1 USD  =  {FormatRate(_coinsPerUsd)} Valcoins", _rateBox,
                          GUILayout.Height(52), GUILayout.ExpandWidth(true));
            GUILayout.Label($"Example: a $5 donation credits about {FormatRate(_coinsPerUsd * 5f)} Valcoins. "
                            + "Other currencies are converted at the same value.", _rateSub);
        }
        else if (_online)
        {
            GUILayout.Box("Exchange rate unavailable", _rateBox, GUILayout.Height(52), GUILayout.ExpandWidth(true));
            GUILayout.Label("The donation service didn't report a rate - ask the operator to update it.", _rateSub);
        }
        GUILayout.Space(8);
    }

    // Compact one-line variant for the Shop tab, where prices are the focus and
    // the rate is only context. Hidden when unknown.
    private void DrawRateNote()
    {
        if (_coinsPerUsd <= 0f) return;
        GUILayout.Label($"Exchange rate: $1 USD = {FormatRate(_coinsPerUsd)} Valcoins.", _sub);
    }

    // Whole rates read as "50"; fractional ones keep one decimal ("52.5").
    private static string FormatRate(float rate) =>
        Mathf.Approximately(rate, Mathf.Round(rate))
            ? Mathf.RoundToInt(rate).ToString()
            : rate.ToString("0.0");

    // ─── Shop tab ─────────────────────────────────────────────────────────

    private Vector2 _shopScroll;
    private void DrawShop()
    {
        if (Catalog.Order.Count == 0)
        {
            GUILayout.Label("The shop is empty - the operator hasn't set up valcoin_shop.yaml yet.", _label);
            return;
        }

        DrawRateNote();

        _shopScroll = GUILayout.BeginScrollView(_shopScroll, GUILayout.ExpandHeight(true));

        // Group SKUs by category, preserving catalog (file) order for both the
        // categories and the items within them. Uncategorised SKUs fall into a
        // trailing "More" group so nothing is ever dropped.
        var catOrder = new List<string>();
        var byCat = new Dictionary<string, List<Catalog.Sku>>();
        foreach (var sku in Catalog.Order)
        {
            var cat = string.IsNullOrEmpty(sku.Category) ? "More" : sku.Category;
            if (!byCat.TryGetValue(cat, out var list))
            {
                list = new List<Catalog.Sku>();
                byCat[cat] = list;
                catOrder.Add(cat);
            }
            list.Add(sku);
        }

        for (int c = 0; c < catOrder.Count; c++)
        {
            var cat = catOrder[c];
            var list = byCat[cat];
            if (c > 0) { GUILayout.Space(6); DrawHr(); GUILayout.Space(4); }
            DrawCategory(cat, list);
        }

        GUILayout.EndScrollView();

        if (!_online)
            GUILayout.Label("Purchasing activates once the donation service is online. "
                            + "Owned perks and weekly limits refresh when it reconnects.", _sub);
    }

    // One category block: header, a single blurb, an optional "you hold N"
    // line for charge pools, then a compact row per item.
    private void DrawCategory(string category, List<Catalog.Sku> skus)
    {
        GUILayout.Label(category.ToUpperInvariant(), _catHdr);

        // Blurb = the first non-empty category_desc among the group's SKUs.
        string blurb = null;
        foreach (var s in skus)
            if (!string.IsNullOrEmpty(s.CategoryDesc)) { blurb = s.CategoryDesc; break; }
        if (!string.IsNullOrEmpty(blurb))
            GUILayout.Label(blurb, _sub);

        // If this group is a charge pool, surface the held count once (not per
        // tier). All tiers of a pool share one Perk key.
        foreach (var s in skus)
            if (s.Effect == "add_charges" && !string.IsNullOrEmpty(s.Perk))
            {
                _charges.TryGetValue(s.Perk, out var held);
                GUILayout.Label($"You currently hold {held} charge(s).", _sub);
                break;
            }

        GUILayout.Space(4);
        foreach (var sku in skus)
            DrawSkuRow(sku);
    }

    // A single compact item row: name + price + one action, with a dim
    // auto-derived contents line and a state note where relevant.
    private void DrawSkuRow(Catalog.Sku sku)
    {
        bool ownedPerk = sku.Effect == "grant_perk" && OwnsSku(sku.Id);
        bool gated     = sku.Effect == "grant_item"
                         && !string.IsNullOrEmpty(sku.RequiresBoss)
                         && !BossGateOk(sku.RequiresBoss);
        int  cap       = sku.WeeklyCap;                       // 0 = unlimited
        int  used      = WeeklyUsed(sku.Id);
        int  remaining = cap > 0 ? Mathf.Max(0, cap - used) : -1;
        bool capReached = cap > 0 && remaining <= 0;

        GUILayout.BeginHorizontal();
        // Optional preview thumbnail at the left of the row. Space is reserved as
        // soon as the SKU declares an image so the row doesn't jump when the
        // async load finishes; the texture is drawn (scaled to fit) once ready.
        if (!string.IsNullOrEmpty(sku.PreviewImage))
        {
            var thumbRect = GUILayoutUtility.GetRect(72, 72, GUILayout.Width(72), GUILayout.Height(72));
            var thumb = ImageCache.Get(sku.PreviewImage);
            if (thumb != null)
            {
                GUI.DrawTexture(thumbRect, thumb, ScaleMode.ScaleToFit);
                // Invisible hit-target over the image — click to view it full-size.
                if (GUI.Button(thumbRect, new GUIContent("", "Click to enlarge"), GUIStyle.none))
                    OpenZoom(sku);
            }
            GUILayout.Space(8);
        }
        // Small tag (sku.Description, e.g. "Best value") rides after the name.
        string tag = string.IsNullOrEmpty(sku.Description) ? "" : $"   ({sku.Description})";
        GUILayout.Label($"{sku.Name}  -  {sku.Price}c{tag}", _label, GUILayout.ExpandWidth(true));

        // Right-hand action column: exactly one of owned / locked / capped /
        // buy / offline. One-time perks lose the Buy button once owned.
        if (ownedPerk)
            GUILayout.Label("Already Purchased", _owned, GUILayout.Width(160));
        else if (gated)
            DisabledButton("Locked", 110);
        else if (capReached)
            DisabledButton("Limit reached", 130);
        else if (_online)
        {
            // Buy only *stages* the purchase — the confirm modal fires the RPC.
            if (GUILayout.Button("Buy", _btn, GUILayout.Width(90), GUILayout.Height(30)))
                _confirmSku = sku;
        }
        else
            DisabledButton("Buy", 90);
        GUILayout.EndHorizontal();

        // Dim auto-derived contents (grant_item only) — what's actually in the
        // bundle, so dropping the per-item prose doesn't hide what you're buying.
        if (sku.Effect == "grant_item")
        {
            var contents = BundleContents(sku.Item);
            if (!string.IsNullOrEmpty(contents))
                GUILayout.Label("    " + contents, _dim);
        }
        else if (sku.Effect == "armor_vfx")
        {
            GUILayout.Label($"    Hovers at your shoulder; renames your helmet \"... {ArmorVfxSuffix(sku)}\"", _dim);
        }

        // State note under the row (gate / weekly cap).
        if (gated)
            GUILayout.Label($"    Unlocks after {FriendlyBoss(sku.RequiresBoss)}", _sub);
        else if (sku.Effect == "grant_item" && cap > 0)
        {
            if (capReached)
                GUILayout.Label($"    Weekly limit reached - resets in {_weekResetsIn}", _sub);
            else
                GUILayout.Label($"    {remaining} of {cap} left this week", _sub);
        }
        GUILayout.Space(8);
    }

    // Turns a grant_item spec ("LoxPie:5,Bread:5") into a readable, ASCII-only
    // contents line ("Lox Pie x5, Bread x5"). CamelCase prefab ids are split on
    // the lower->upper boundary; qty defaults to 1 when omitted.
    private static readonly Regex CamelBoundary = new Regex(@"(?<=[a-z0-9])(?=[A-Z])", RegexOptions.Compiled);
    private static string BundleContents(string itemSpec)
    {
        if (string.IsNullOrEmpty(itemSpec)) return "";
        var parts = new List<string>();
        foreach (var raw in itemSpec.Split(','))
        {
            var piece = raw.Trim();
            if (piece.Length == 0) continue;

            string prefab = piece;
            string qty = "1";
            int colon = piece.LastIndexOf(':');
            if (colon > 0)
            {
                prefab = piece.Substring(0, colon).Trim();
                var q = piece.Substring(colon + 1).Trim();
                if (q.Length > 0) qty = q;
            }

            string pretty = CamelBoundary.Replace(prefab, " ");
            parts.Add(qty == "1" ? pretty : $"{pretty} x{qty}");
        }
        return string.Join(", ", parts.ToArray());
    }

    // ─── Gift tab ─────────────────────────────────────────────────────────

    private void DrawGift()
    {
        GUILayout.Label("Gift Valcoins", _hdr);
        GUILayout.Label("Send Valcoins to another player on the server.", _label);
        GUILayout.Space(4);
        GUILayout.BeginHorizontal();
        GUILayout.Label("To:", _label, GUILayout.Width(90));
        _giftTo = GUILayout.TextField(_giftTo ?? "", GUILayout.Width(200));
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        GUILayout.Label("Amount:", _label, GUILayout.Width(90));
        _giftAmount = GUILayout.TextField(_giftAmount ?? "", GUILayout.Width(120));
        GUILayout.EndHorizontal();

        GUILayout.Space(6);
        if (_online)
        {
            if (GUILayout.Button("Send gift", _btn, GUILayout.Height(28), GUILayout.Width(140)))
            {
                if (string.IsNullOrWhiteSpace(_giftTo) || string.IsNullOrWhiteSpace(_giftAmount))
                    PushLog("Fill in both fields.");
                else
                    RpcLayer.SendAction($"gift:{_giftTo.Trim()}:{_giftAmount.Trim()}");
            }
        }
        else
        {
            GUILayout.Label("Send gift", _btnDim, GUILayout.Height(28), GUILayout.Width(140));
        }
    }

    // ─── Patrons tab ────────────────────────────────────────────────────────

    private void DrawPatrons()
    {
        GUILayout.Label("Top Patrons", _hdr);
        GUILayout.Space(4);
        if (!_online)
        {
            GUILayout.Label("The patron leaderboard appears here once the donation service is online.", _label);
            return;
        }
        if (_topDonors.Count == 0)
        {
            GUILayout.Label("No patrons yet - be the first! Head to the Donate tab.", _label);
        }
        else
        {
            foreach (var e in _topDonors)
                GUILayout.Label($"  {e.rank}.  {e.name ?? "Anonymous"}  -  {e.total_coins} coins", _label);
        }
        GUILayout.Space(8);
        if (GUILayout.Button("Refresh", _btn, GUILayout.Width(90)))
        {
            _topDonors.Clear();
            RefreshStateSoon(force: true);
        }
    }

    // ─── Admin tab ──────────────────────────────────────────────────────────

    private void DrawAdmin()
    {
        GUILayout.Label("Manually adjust a player's Valcoin balance.", _label);
        GUILayout.Label("Give adds Valcoins to the player; Remove subtracts them "
                        + "(e.g. to correct a mistake or claw back an abuse).", _sub);
        GUILayout.Space(4);
        GUILayout.BeginHorizontal();
        GUILayout.Label("Player:", _label, GUILayout.Width(90));
        _adminTarget = GUILayout.TextField(_adminTarget ?? "", GUILayout.Width(200));
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        GUILayout.Label("Amount:", _label, GUILayout.Width(90));
        _adminAmount = GUILayout.TextField(_adminAmount ?? "", GUILayout.Width(120));
        GUILayout.EndHorizontal();

        GUILayout.Space(6);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Give", _btn, GUILayout.Height(28), GUILayout.Width(100))) SendAdminAdjust(give: true);
        if (GUILayout.Button("Remove", _btn, GUILayout.Height(28), GUILayout.Width(100))) SendAdminAdjust(give: false);
        GUILayout.EndHorizontal();
    }

    private void SendAdminAdjust(bool give)
    {
        if (string.IsNullOrWhiteSpace(_adminTarget) || string.IsNullOrWhiteSpace(_adminAmount))
        { PushLog("Fill in both fields."); return; }
        string action = (give ? "admin_give:" : "admin_remove:") + $"{_adminTarget.Trim()}:{_adminAmount.Trim()}";
        RpcLayer.SendAction(action);
    }

    // ─── message log ──────────────────────────────────────────────────────

    private void DrawLog()
    {
        GUILayout.Label("Messages", _label);
        _logScroll = GUILayout.BeginScrollView(_logScroll, GUILayout.Height(80));
        for (int i = _log.Count - 1; i >= 0; i--)
            GUILayout.Label("- " + _log[i], _logLine);
        GUILayout.EndScrollView();
    }

    private void PushLog(string msg)
    {
        _log.Add(msg);
        if (_log.Count > LogCap) _log.RemoveAt(0);
    }

    // ─── Image zoom overlay ─────────────────────────────────────────────────

    private void OpenZoom(Catalog.Sku sku)
    {
        _zoomImage   = sku.PreviewImage;
        _zoomCaption = sku.Name;
    }

    // Full-size preview over a dimmed screen. The image is fitted into a box
    // that never exceeds the window (or the texture's own size, so a small
    // source isn't blown up into a blurry mess), and closes on click anywhere,
    // the Close button, or Escape.
    private void DrawZoomModal()
    {
        var tex = ImageCache.Get(_zoomImage);
        if (tex == null) { _zoomImage = null; return; }

        GUI.Box(new Rect(0, 0, Screen.width, Screen.height), GUIContent.none, _line);

        if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
        { _zoomImage = null; Event.current.Use(); return; }

        // Fit the texture inside the available area, preserving aspect and never
        // upscaling past 1:1.
        float maxW = Screen.width  * 0.8f;
        float maxH = Screen.height * 0.8f - 70f;   // leave room for caption + button
        float scale = Mathf.Min(maxW / tex.width, maxH / tex.height, 1f);
        float imgW = tex.width * scale, imgH = tex.height * scale;

        float panelW = imgW + 36f;
        float panelH = imgH + 100f;
        var r = new Rect((Screen.width - panelW) / 2f, (Screen.height - panelH) / 2f, panelW, panelH);

        // Click outside the panel closes. Handled before the panel's own controls
        // so a full-screen hit-target can't swallow the Close button.
        if (Event.current.type == EventType.MouseDown && !r.Contains(Event.current.mousePosition))
        { _zoomImage = null; Event.current.Use(); return; }

        GUI.Box(r, GUIContent.none, _bg);

        GUI.DrawTexture(new Rect(r.x + 18f, r.y + 18f, imgW, imgH), tex, ScaleMode.ScaleToFit);

        if (!string.IsNullOrEmpty(_zoomCaption))
            GUI.Label(new Rect(r.x + 18f, r.y + imgH + 22f, imgW, 24f), _zoomCaption, _rateSub);

        if (GUI.Button(new Rect(r.x + panelW / 2f - 60f, r.y + imgH + 50f, 120f, 32f), "Close", _btn))
            _zoomImage = null;
    }

    // ─── Purchase confirmation modal ────────────────────────────────────────

    private void DrawConfirmModal()
    {
        var sku = _confirmSku;
        if (sku == null) return;

        // Dim backdrop over the whole screen.
        GUI.Box(new Rect(0, 0, Screen.width, Screen.height), GUIContent.none, _line);

        bool isCharge = sku.Effect == "add_charges";
        bool isVfx    = sku.Effect == "armor_vfx";

        // armor_vfx: does the equipped helmet already carry a familiar? Then
        // this purchase overwrites it — warn before taking the coins.
        string overwriteWarn = null;
        if (isVfx)
        {
            var curAura = ArmorVfx.EquippedAura(Player.m_localPlayer, "head");
            if (curAura != null && ArmorVfx.Registry.TryGetValue(curAura, out var curDef))
                overwriteWarn = curAura == sku.Perk
                    ? $"Your equipped helmet already has the {curDef.Display} familiar bound to it."
                    : $"Warning: your equipped helmet already has the {curDef.Display} familiar bound to it. Buying this will overwrite it with {ArmorVfxDisplay(sku)}.";
        }

        // Reserve room for the preview thumbnail (if any) so the modal grows to
        // fit it rather than clipping the buttons.
        int previewH = string.IsNullOrEmpty(sku.PreviewImage) ? 0 : 230;

        int w = Mathf.Min(isVfx ? 480 : 460, Screen.width - 60);
        int baseH = isCharge ? 300 : (isVfx ? (overwriteWarn != null ? 400 : 340) : 240);
        int h = Mathf.Min(baseH + previewH, Screen.height - 60);
        var r = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
        GUI.Box(r, GUIContent.none, _bg);

        GUILayout.BeginArea(new Rect(r.x + 18, r.y + 18, r.width - 36, r.height - 36));

        GUILayout.Label("Confirm Purchase", _hdr);
        DrawHr();
        GUILayout.Space(8);

        // Preview (centered) when the SKU has one — click it to view full-size.
        if (!string.IsNullOrEmpty(sku.PreviewImage))
        {
            var pr = GUILayoutUtility.GetRect(190, 190, GUILayout.Height(190), GUILayout.ExpandWidth(true));
            var tex = ImageCache.Get(sku.PreviewImage);
            if (tex != null)
            {
                GUI.DrawTexture(pr, tex, ScaleMode.ScaleToFit);
                if (GUI.Button(pr, new GUIContent("", "Click to enlarge"), GUIStyle.none))
                    OpenZoom(sku);
                GUILayout.Label("(click the image to enlarge)", _rateSub);
            }
            GUILayout.Space(6);
        }

        GUILayout.Label($"Buy \"{sku.Name}\" for {sku.Price} Valcoins?", _label);
        GUILayout.Space(4);
        GUILayout.Label($"Your balance: {_balance} Valcoins", _sub);

        // Soulkeeper (and any charge SKU): the pool is credited server-side and
        // reflected on the next state poll, so set the expectation up front.
        if (isCharge)
        {
            GUILayout.Space(8);
            GUILayout.Label("Note: charges are processed on the server - it may take "
                            + "a few seconds for your new charge count to appear.", _sub);
        }

        // armor_vfx: familiars bind to the equipped helmet.
        if (isVfx)
        {
            GUILayout.Space(8);
            GUILayout.Label("The familiar is bound to your equipped helmet and hovers at your shoulder.", _label);
            string stats = ArmorVfxStats(sku);
            GUILayout.Label("You must have a helmet equipped. It is renamed "
                            + $"\"... {ArmorVfxSuffix(sku)}\"."
                            + (stats != "" ? $" Grants feather fall and {stats}." : ""), _sub);
            if (overwriteWarn != null)
            {
                GUILayout.Space(6);
                var pc = GUI.color;
                GUI.color = new Color(1f, 0.6f, 0.4f);
                GUILayout.Label(overwriteWarn, _label);
                GUI.color = pc;
            }
        }

        GUILayout.FlexibleSpace();
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Yes, buy", _btnPrimary, GUILayout.Height(38)))
        {
            RpcLayer.SendAction("buy:" + sku.Id);
            // Arm the result modal: the next server reply reports the outcome.
            _pendingBuySku = sku.Id;
            _pendingBuyDeadline = Time.realtimeSinceStartup + 12f;
            _resultExtra = null;
            _confirmSku = null;
        }
        GUILayout.Space(10);
        if (GUILayout.Button("Cancel", _btn, GUILayout.Height(38)))
            _confirmSku = null;
        GUILayout.EndHorizontal();

        GUILayout.EndArea();
    }

    // The armor-effect name suffix for a SKU (from the aura registry via perk).
    private static string ArmorVfxSuffix(Catalog.Sku sku)
        => ArmorVfx.Registry.TryGetValue(sku.Perk ?? "", out var a) ? a.Suffix : "of ...";

    private static string ArmorVfxDisplay(Catalog.Sku sku)
        => ArmorVfx.Registry.TryGetValue(sku.Perk ?? "", out var a) ? a.Display : sku.Name;

    private static string ArmorVfxStats(Catalog.Sku sku)
        => ArmorVfx.Registry.TryGetValue(sku.Perk ?? "", out var a) ? ArmorVfx.StatsText(a) : "";

    // ─── Terms of Use modal ─────────────────────────────────────────────────

    private void DrawTermsModal(Rect parent)
    {
        // Dim backdrop over the whole screen.
        GUI.Box(new Rect(0, 0, Screen.width, Screen.height), GUIContent.none, _line);

        int w = Mathf.Min(560, Screen.width - 60);
        int h = Mathf.Min(460, Screen.height - 60);
        var r = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
        GUI.Box(r, GUIContent.none, _bg);

        GUILayout.BeginArea(new Rect(r.x + 16, r.y + 16, r.width - 32, r.height - 32));

        GUILayout.BeginHorizontal();
        GUILayout.Label("Terms of Use - Donations", _hdr);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("X", _btn, GUILayout.Width(30))) _showTerms = false;
        GUILayout.EndHorizontal();
        DrawHr();

        _termsScroll = GUILayout.BeginScrollView(_termsScroll, GUILayout.ExpandHeight(true));
        foreach (var line in TermsText)
        {
            if (line.Length == 0) GUILayout.Space(6);
            else GUILayout.Label(line, _label);
        }
        GUILayout.EndScrollView();

        GUILayout.Space(6);
        if (GUILayout.Button("I understand", _btnPrimary, GUILayout.Height(38))) _showTerms = false;

        GUILayout.EndArea();
    }

    private static readonly string[] TermsText =
    {
        "Please read these terms before donating. By making a donation you agree to all of the following.",
        "",
        "1. Voluntary support. Donations are entirely voluntary gifts to help cover server costs. They are not a purchase of goods or services.",
        "",
        "2. No real-world value. Valcoins, perks, and any in-game items are virtual and have no monetary value. They cannot be sold, traded for cash, or redeemed outside this server.",
        "",
        "3. Non-refundable. All donations are final and non-refundable, except where required by law. Initiating a chargeback may result in loss of Valcoins, perks, and access to the server.",
        "",
        "4. No pay-to-win. Perks are cosmetic or convenience only, and consumables are weekly-limited and earnable in normal play. Donating does not grant a competitive advantage.",
        "",
        "5. Subject to change. The server operators may adjust prices, perks, the Valcoin economy, or discontinue the donation system at any time without notice.",
        "",
        "6. No guarantee of service. Donating does not guarantee uninterrupted server availability, specific uptime, or that the server will continue to run for any period of time.",
        "",
        "7. Eligibility. You must be of legal age in your jurisdiction, or have permission from a parent or guardian, and use your own valid payment method.",
        "",
        "8. Conduct. Donations do not exempt any player from server rules. Perks may be revoked for rule violations without refund.",
        "",
        "9. Not affiliated. This is a community server and is not affiliated with, endorsed by, or sponsored by Iron Gate, Coffee Stain, or the payment providers.",
        "",
        "10. Contact. For questions about a donation, contact a server administrator. Any refunds are granted solely at the operators' discretion.",
        "",
        "Thank you for supporting the realm!",
    };

    // ─── shop-state helpers ─────────────────────────────────────────────────

    private bool OwnsSku(string skuId) =>
        !string.IsNullOrEmpty(skuId) && _ownedSkus.Contains(skuId);

    private int WeeklyUsed(string skuId) =>
        (!string.IsNullOrEmpty(skuId) && _weeklyUsage.TryGetValue(skuId, out var c)) ? c : 0;

    // Client-side boss gate. Global keys replicate to connected clients, so this
    // mirrors ShopHandler.BossGateSatisfied and fails open if the key system
    // isn't ready yet (gating is a balance nicety, not a security control — the
    // server re-checks at purchase time regardless).
    private static bool BossGateOk(string bossKey)
    {
        if (string.IsNullOrEmpty(bossKey)) return true;
        try
        {
            if (ZoneSystem.instance == null) return true;
            return ZoneSystem.instance.GetGlobalKey(bossKey);
        }
        catch { return true; }
    }

    // A greyed, non-interactive button placeholder used for locked / capped /
    // offline states so they read like a disabled Buy button.
    private void DisabledButton(string label, float width)
    {
        GUILayout.Label(label, _btnDim, GUILayout.Width(width), GUILayout.Height(30));
    }

    // "defeated_bonemass" -> "Bonemass" for the gate note.
    private static string FriendlyBoss(string key)
    {
        switch (key)
        {
            case "defeated_eikthyr":    return "Eikthyr";
            case "defeated_gdking":     return "The Elder";
            case "defeated_bonemass":   return "Bonemass";
            case "defeated_dragon":     return "Moder";
            case "defeated_goblinking": return "Yagluth";
            case "defeated_queen":      return "The Queen";
            case "defeated_fader":      return "the Ashlands boss";
            default:
                var s = (key != null && key.StartsWith("defeated_"))
                    ? key.Substring("defeated_".Length) : (key ?? "");
                return s.Length > 0 ? char.ToUpper(s[0]) + s.Substring(1) : (key ?? "");
        }
    }

}
