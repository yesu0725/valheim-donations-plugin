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

    // Buffer for server-pushed messages (buy/gift/admin results).
    private readonly List<string> _log = new List<string>();
    private const int LogCap = 12;
    private Vector2 _logScroll;

    // Gift / title text fields.
    private string _giftTo = "", _giftAmount = "";
    private string _adminTarget = "", _adminAmount = "";

    private GUIStyle _bg, _hdr, _sub, _btn, _btnActive, _btnDim, _btnPrimary,
                     _line, _logLine, _label, _codeBox, _linkBtn, _pillOn, _pillOff,
                     _owned;
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

        // Everything else is a buy/gift/admin result line.
        _log.Add(msg);
        if (_log.Count > LogCap) _log.RemoveAt(0);
        RefreshStateSoon();
    }

    // ─── styling ──────────────────────────────────────────────────────────

    private void InitStyles()
    {
        _bg = new GUIStyle(GUI.skin.box);
        _bg.normal.background = SolidTex(new Color(0.08f, 0.07f, 0.05f, 0.98f));
        _bg.padding = new RectOffset(12, 12, 12, 12);

        _hdr = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold };
        _hdr.normal.textColor = new Color(0.87f, 0.72f, 0.42f);

        _sub = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Italic, wordWrap = true };
        _sub.normal.textColor = new Color(0.75f, 0.72f, 0.62f);

        _btn = new GUIStyle(GUI.skin.button) { fontSize = 15 };
        _btn.padding = new RectOffset(10, 10, 7, 7);
        _btn.normal.textColor = new Color(0.94f, 0.92f, 0.86f);
        _btn.hover.textColor  = Color.white;

        _btnActive = new GUIStyle(_btn);
        _btnActive.normal.background = SolidTex(new Color(0.6f, 0.45f, 0.2f, 1f));
        _btnActive.normal.textColor = Color.white;

        _btnDim = new GUIStyle(_btn);
        _btnDim.normal.textColor = new Color(0.5f, 0.48f, 0.42f);

        // Prominent, high-contrast primary action (the donate button).
        // Vertical padding kept modest so the label sits centered at a normal
        // button height instead of floating in an oversized gold slab.
        _btnPrimary = new GUIStyle(GUI.skin.button) { fontSize = 16, fontStyle = FontStyle.Bold };
        _btnPrimary.alignment = TextAnchor.MiddleCenter;
        _btnPrimary.padding = new RectOffset(12, 12, 6, 6);
        _btnPrimary.normal.background = SolidTex(new Color(0.78f, 0.6f, 0.22f, 1f));
        _btnPrimary.normal.textColor = new Color(0.12f, 0.08f, 0.02f);   // near-black on gold, high contrast
        _btnPrimary.hover.background = SolidTex(new Color(0.9f, 0.71f, 0.28f, 1f));
        _btnPrimary.hover.textColor = Color.black;

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

        // Terms modal draws last so it sits on top of everything.
        if (_showTerms) DrawTermsModal(rect);
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
        GUILayout.Space(6);

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

    // ─── Shop tab ─────────────────────────────────────────────────────────

    private Vector2 _shopScroll;
    private void DrawShop()
    {
        if (Catalog.Order.Count == 0)
        {
            GUILayout.Label("The shop is empty - the operator hasn't set up valcoin_shop.yaml yet.", _label);
            return;
        }

        _shopScroll = GUILayout.BeginScrollView(_shopScroll, GUILayout.ExpandHeight(true));

        foreach (var sku in Catalog.Order)
        {
            // Resolve this SKU's state from server-provided data.
            bool ownedPerk = sku.Effect == "grant_perk" && OwnsSku(sku.Id);
            bool gated     = sku.Effect == "grant_item"
                             && !string.IsNullOrEmpty(sku.RequiresBoss)
                             && !BossGateOk(sku.RequiresBoss);
            int  cap       = sku.WeeklyCap;                       // 0 = unlimited
            int  used      = WeeklyUsed(sku.Id);
            int  remaining = cap > 0 ? Mathf.Max(0, cap - used) : -1;
            bool capReached = cap > 0 && remaining <= 0;

            GUILayout.BeginHorizontal();
            GUILayout.Label($"{sku.Name}  -  {sku.Price}c", _label, GUILayout.ExpandWidth(true));

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
                if (GUILayout.Button("Buy", _btn, GUILayout.Width(90), GUILayout.Height(30)))
                    RpcLayer.SendAction("buy:" + sku.Id);
            }
            else
                DisabledButton("Buy", 90);
            GUILayout.EndHorizontal();

            // Status note under the row.
            if (gated)
                GUILayout.Label($"    Unlocks after {FriendlyBoss(sku.RequiresBoss)}", _sub);
            else if (sku.Effect == "grant_item" && cap > 0)
            {
                if (capReached)
                    GUILayout.Label($"    Weekly limit reached - resets in {_weekResetsIn}", _sub);
                else
                    GUILayout.Label($"    {remaining} of {cap} left this week", _sub);
            }
            else if (sku.Effect == "add_charges" && !string.IsNullOrEmpty(sku.Perk))
            {
                _charges.TryGetValue(sku.Perk, out var held);
                GUILayout.Label($"    You hold {held} charge(s)", _sub);
            }

            if (!string.IsNullOrEmpty(sku.Description))
                GUILayout.Label("    " + sku.Description, _label);
            GUILayout.Space(10);
        }
        GUILayout.EndScrollView();

        if (!_online)
            GUILayout.Label("Purchasing activates once the donation service is online. "
                            + "Owned perks and weekly limits refresh when it reconnects.", _sub);
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
