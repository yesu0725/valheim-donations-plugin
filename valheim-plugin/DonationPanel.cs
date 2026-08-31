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
    private int _questEarned, _questCap, _questStreak;
    private string _questResetsIn = "";
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
    private float  _donateWaitingSince = -1f;    // >=0 while a request is in flight
    private float  _copiedFlashUntil;            // "Copied!" transient
    private const float DonateCooldownSeconds = 30f;

    // How long to wait for the server's reply before giving up. The server's own
    // HTTP call to the backend times out at 15s (BackendClient.Send), so anything
    // past 20s means the reply is never coming — the action reached the server but
    // its answer didn't come back. Without this the panel sat on "Requesting your
    // code..." forever, which reads as a dead button and hides a server-side fault.
    private const float DonateReplyTimeoutSeconds = 20f;

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

    // Purchase-result modal. After "Yes" a purchase is armed and a "working on
    // it" modal goes up; the server's __BUYRES__ reply turns it into the outcome.
    private string _pendingBuySku;
    private float  _pendingBuyStarted;
    private float  _pendingBuyDeadline;
    private string _resultText;      // non-null => result modal is up
    private BuyOutcome _resultKind;
    private string _resultExtra;     // e.g. armor-effect apply outcome

    // What the server says became of a purchase. "Unknown" is a real answer and
    // must never be dressed up as either of the others: it is the state where the
    // coins may or may not have moved, and telling the player it failed while
    // their balance dropped is the exact bug this replaced.
    private enum BuyOutcome { Success, Failed, Unknown }

    // How long to wait for a purchase verdict before giving up on it. The server
    // may spend two BackendClient timeouts (15s each) plus a retry gap resolving
    // an unanswered spend, and it MUST be allowed to finish: this was 12s, so it
    // expired first and painted "Purchase Failed" over a spend that then went
    // through and took the coins. Whatever this number is, it has to be larger
    // than the server's own worst case, never smaller.
    private const float BuyReplyTimeoutSeconds = 40f;

    // The last plain line the server sent, held so the verdict marker that
    // follows it can label it. Also how a verdict that arrives after the player
    // dismissed the modal (an automatic refund) still gets shown.
    private string _lastPlainMsg;
    private float  _lastPlainAt;
    private const float LateVerdictSeconds = 8f;

    // Buffer for server-pushed messages (buy/gift/admin results).
    private readonly List<string> _log = new List<string>();
    private const int LogCap = 12;
    private Vector2 _logScroll;

    // Gift / title text fields.
    private string _giftTo = "", _giftAmount = "";
    private string _adminTarget = "", _adminAmount = "";

    private GUIStyle _bg, _hdr, _sub, _btn, _btnActive, _btnDim, _btnPrimary,
                     _bgFill, _line, _scrim, _logLine, _label, _codeBox, _linkBtn, _pillOn, _pillOff,
                     _owned, _catHdr, _dim, _rateBox, _rateSub;
    private bool _stylesReady;

    // ValheimTheme.Version the current styles were built from. They are rebuilt
    // when it moves, so a resolution or GUI-scale change re-themes the panel
    // without a restart.
    private int _styleVersion = -1;

    // How far content sits inside the panel frame. Derived from the 9-slice
    // border of the game's own panel sprite, because that frame is carved and
    // chunky -- the old flat 14px inset would have laid text on top of it.
    private int _contentInset = 14;

    private float _lastStateFetch;
    private const float AutoRefreshSeconds = 20f;
    private bool _online;

    // ─── lifecycle ────────────────────────────────────────────────────────

    // The single live panel (one per client, spawned in Plugin.SpawnUiWhenZnetReady).
    // Exposed so UI entry points outside this class -- the inventory-screen button in
    // InventoryMenuButton.cs -- can open it without owning a reference.
    public static DonationPanel Instance { get; private set; }

    // Deferred open, armed by RequestOpen(). The panel refuses to draw while the
    // inventory is up (see OnGUI), so an open asked for FROM the inventory screen
    // has to wait for that screen to actually go away -- Hide() isn't instant.
    private float _openRequestUntil;
    private const float OpenRequestGraceSeconds = 2f;

    private void Awake()
    {
        if (!string.IsNullOrEmpty(Config.CodexToggleKey)
            && Enum.TryParse<KeyCode>(Config.CodexToggleKey, true, out var k))
            _toggleKey = k;

        Instance = this;
        RpcLayer.OnPanelMessage += OnServerMessage;
        DontDestroyOnLoad(gameObject);
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        RpcLayer.OnPanelMessage -= OnServerMessage;
        DonationUiState.PanelOpen = false;
    }

    // Ask for the panel to open as soon as the screen is clear. Safe to call from a
    // Unity UI click handler in the same frame the inventory is dismissed: the
    // request is honoured in Update() once nothing else owns the screen, and lapses
    // after a short grace period if something (a menu, the map) stays up instead.
    public static void RequestOpen()
    {
        var panel = Instance;
        if (panel == null)
        {
            Debug.LogWarning("[Valcoin] Donation panel isn't spawned yet; ignoring open request.");
            return;
        }
        panel._openRequestUntil = Time.realtimeSinceStartup + OpenRequestGraceSeconds;
    }

    private bool _wasOpen;

    private void Update()
    {
        if (Input.GetKeyDown(_toggleKey)) Toggle();

        if (_openRequestUntil > 0f)
        {
            if (Time.realtimeSinceStartup > _openRequestUntil) _openRequestUntil = 0f;
            else if (ScreenIsClear())
            {
                _openRequestUntil = 0f;
                if (!_open) Toggle();
            }
        }

        if (_open)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible   = true;
            DonationUiState.SetMouseCapture(false);
            RefreshStateSoon();
            // Deliberately here and not in OnGUI: extraction blits through a
            // RenderTexture, and doing that inside the IMGUI render loop means
            // swapping the active render target out from under the GUI.
            ValheimTheme.Ensure();
        }
        else if (_wasOpen)
        {
            DonationUiState.SetMouseCapture(true);
        }

        if (_open != _wasOpen) { DonationUiState.PanelOpen = _open; _wasOpen = _open; }
    }

    // Nothing else owns the screen: no game menu, no inventory, no map. Both the
    // "should I still be drawing?" check in OnGUI and the deferred open in Update
    // read this, so they can never disagree about what counts as blocked.
    private static bool ScreenIsClear()
        => !Menu.IsVisible()
           && !(InventoryGui.instance != null && InventoryGui.IsVisible())
           && !(Minimap.instance != null && Minimap.IsOpen());

    private void Toggle()
    {
        _open = !_open;
        if (_open)
        {
            RefreshStateSoon(force: true);
            ValheimTheme.Ensure();   // so the first frame is already themed
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
        public int    quest_daily_earned;
        public int    quest_daily_cap;
        public string quest_resets_in;
        public int    quest_streak;
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
                _questEarned = r.quest_daily_earned;
                _questCap = r.quest_daily_cap;
                _questResetsIn = r.quest_resets_in ?? "";
                _questStreak = r.quest_streak;
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
    private const string BuyResultPrefix    = "__BUYRES__:";

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
            _donateWaitingSince = -1f;   // reply arrived; stop the timeout clock
            return;
        }

        if (msg.StartsWith(DonateErrPrefix))
        {
            _donateStatus = msg.Substring(DonateErrPrefix.Length);
            _donateCooldownUntil = 0f;   // let them retry immediately after a failure
            _donateWaitingSince = -1f;
            return;
        }

        if (msg.StartsWith(ArmorVfxPrefix))
        {
            // Format: <aura>:<slot>[:<sku>]. The spend already succeeded
            // server-side; apply the cosmetic + rename on this (the buyer's)
            // client, and log whatever it reports.
            var vfx = msg.Substring(ArmorVfxPrefix.Length).Split(new[] { ':' }, 3);
            string m;
            bool applied = false;
            if (vfx.Length >= 2) applied = ArmorVfx.ApplyToEquipped(vfx[0], vfx[1], out m);
            else m = "Armor effect could not be applied.";

            // Only this client can know whether the aura landed, because only this
            // client knows what is equipped. If it didn't, say so — the server
            // holds a one-shot ticket for exactly this and refunds on hearing it.
            if (!applied && vfx.Length >= 3 && !string.IsNullOrEmpty(vfx[2]))
                RpcLayer.SendAction("buyfail:" + vfx[2]);

            PushLog(m);
            // Fold the apply outcome into the pending purchase-result modal.
            if (_pendingBuySku != null) _resultExtra = m;
            return;
        }

        // The server's verdict on the line it just sent. It carries no text of its
        // own (see ShopHandler.ResultPrefix) — it re-labels the message already
        // shown, which is what lets a server older than this client still work.
        if (msg.StartsWith(BuyResultPrefix))
        {
            string kind = msg.Substring(BuyResultPrefix.Length).Trim();
            var verdict = kind == "ok"   ? BuyOutcome.Success
                        : kind == "hold" ? BuyOutcome.Unknown
                                         : BuyOutcome.Failed;

            // Normally this upgrades the provisional reading of the line that
            // arrived a moment ago. It also covers a verdict that lands after the
            // player dismissed the modal — an automatic refund, say — by promoting
            // that line into a fresh modal rather than leaving it buried in the log.
            if (!string.IsNullOrEmpty(_lastPlainMsg)
                && Time.realtimeSinceStartup - _lastPlainAt < LateVerdictSeconds)
            {
                _pendingBuySku = null;
                _resultKind = verdict;
                _resultText = string.IsNullOrEmpty(_resultExtra)
                    ? _lastPlainMsg : _lastPlainMsg + "\n\n" + _resultExtra;
                _resultExtra = null;
                _lastPlainMsg = null;
            }
            else if (_resultText != null)
            {
                _resultKind = verdict;   // modal still up from that same line
            }
            RefreshStateSoon();
            return;
        }

        // A plain line: a buy/gift/admin result, or the sentence a verdict marker
        // is about to label.
        PushLog(msg);
        _lastPlainMsg = msg;
        _lastPlainAt  = Time.realtimeSinceStartup;

        // If a purchase is waiting on an answer, this line IS the answer — read it
        // now with the pre-5.22 heuristic and let the marker correct the label if
        // one follows.
        //
        // THIS IS WHAT A 5.21.x SERVER SENDS, AND ONLY THIS. Without this branch a
        // 5.22 client on an older server sat through the whole 40s window and then
        // announced "Purchase Unconfirmed" for a purchase that had plainly
        // succeeded and been paid for — the marker it was waiting for was never
        // coming. A client must never depend on a server-side message it cannot be
        // sure the server knows how to send.
        if (_pendingBuySku != null)
        {
            _pendingBuySku = null;
            _resultKind = (msg.StartsWith("Purchased") || msg.Contains("was already processed"))
                ? BuyOutcome.Success : BuyOutcome.Failed;
            _resultText = string.IsNullOrEmpty(_resultExtra) ? msg : msg + "\n\n" + _resultExtra;
            _resultExtra = null;
        }
        RefreshStateSoon();
    }

    // ─── styling ──────────────────────────────────────────────────────────

    // Builds every GUIStyle the panel uses.
    //
    // The look is NOT authored here. ValheimTheme reads the player inventory
    // screen and hands over the game's real panel sprite, its real button sprite
    // in all four states, its serif font, its text colours and the font sizes it
    // renders at (already converted to screen pixels for the player's resolution
    // and GUI scale). This method's whole job is to spend those on the right
    // controls, and to fall back to the old hand-drawn approximation field by
    // field for anything the game did not give up.
    //
    // Called again whenever ValheimTheme.Version changes, which is how a
    // mid-session resolution or GUI-scale change re-themes the panel live.
    private void InitStyles()
    {
        int body   = ValheimTheme.BodySize;
        int small  = ValheimTheme.SmallSize;
        int header = ValheimTheme.HeaderSize;
        int btnTxt = ValheimTheme.ButtonSize;

        var text   = ValheimTheme.TextColor;        // cream
        var dim    = ValheimTheme.DimColor;
        var gold   = ValheimTheme.HeaderColor;
        var onBtn  = ValheimTheme.ButtonTextColor;

        // Panel: the inventory's own wood frame, 9-sliced by its own border so the
        // carved corners stay the right size however tall the panel gets.
        _bg = new GUIStyle(GUI.skin.box);
        if (ValheimTheme.PanelTex != null)
        {
            _bg.normal.background = ValheimTheme.PanelTex;
            _bg.border = ValheimTheme.PanelBorder;
            var b = ValheimTheme.PanelBorder;
            _contentInset = Mathf.Clamp(Mathf.Max(Mathf.Max(b.left, b.right), Mathf.Max(b.top, b.bottom)) + 2, 14, 30);
        }
        else
        {
            _bg.normal.background = BorderTex(ValheimTheme.Wood, ValheimTheme.Trim, 3, 24);
            _bg.border = new RectOffset(4, 4, 4, 4);
            _contentInset = 16;
        }
        _bg.padding = new RectOffset(_contentInset, _contentInset, _contentInset, _contentInset);

        // Opaque wood laid down under the frame. The game composites its panels
        // from a backdrop plus a border sprite, so the sprite we lift may well be
        // frame-only with a see-through middle — which would put the panel's text
        // straight over the running game. Painting the fill ourselves makes the
        // panel opaque whatever we picked up, and costs one extra box per draw.
        _bgFill = new GUIStyle();
        _bgFill.normal.background = SolidTex(ValheimTheme.Wood);

        _hdr = new GUIStyle(GUI.skin.label) { fontSize = header, fontStyle = FontStyle.Bold };
        _hdr.normal.textColor = gold;

        _sub = new GUIStyle(GUI.skin.label) { fontSize = small, fontStyle = FontStyle.Italic, wordWrap = true };
        _sub.normal.textColor = dim;

        // Buttons: the inventory's "Take All" button, in its own four states.
        _btn = new GUIStyle(GUI.skin.button) { fontSize = btnTxt };
        if (ValheimTheme.ButtonTex != null)
        {
            _btn.normal.background = ValheimTheme.ButtonTex;
            _btn.hover.background  = ValheimTheme.ButtonHoverTex ?? ValheimTheme.ButtonTex;
            _btn.active.background = ValheimTheme.ButtonActiveTex ?? ValheimTheme.ButtonHoverTex;
            _btn.border = ValheimTheme.ButtonBorder;
            var bb = ValheimTheme.ButtonBorder;
            _btn.padding = new RectOffset(Mathf.Max(10, bb.left), Mathf.Max(10, bb.right), 6, 6);
        }
        else
        {
            _btn.border = new RectOffset(3, 3, 3, 3);
            _btn.padding = new RectOffset(10, 10, 7, 7);
            _btn.normal.background = BorderTex(ValheimTheme.WoodDark,  ValheimTheme.Trim, 2);
            _btn.hover.background  = BorderTex(ValheimTheme.WoodLight, ValheimTheme.Trim, 2);
            _btn.active.background = BorderTex(ValheimTheme.WoodDark,  ValheimTheme.Gold, 2);
        }
        _btn.normal.textColor = onBtn;
        _btn.hover.textColor  = onBtn;
        _btn.active.textColor = onBtn;

        // Selected tab. In the game, an active tab is marked by GOLD TEXT and a
        // gold edge, not by a brighter slab — the wood stays wood. An earlier cut
        // multiplied the button texture by 1.5 to make it "pop", which is most of
        // why the panel came out glowing orange; the tint here is a nudge, and the
        // colour does the talking.
        _btnActive = new GUIStyle(_btn);
        var litTab = ValheimTheme.ButtonVariant(new Color(1.08f, 1.04f, 0.98f, 1f));
        _btnActive.normal.background = litTab
            ?? BorderTex(ValheimTheme.WoodLight, ValheimTheme.Gold, 2);
        _btnActive.hover.background  = _btnActive.normal.background;
        _btnActive.active.background = _btnActive.normal.background;
        _btnActive.normal.textColor  = gold;
        _btnActive.hover.textColor   = gold;
        _btnActive.active.textColor  = gold;

        // Disabled/inert button (owned/locked/capped states) — the game's own
        // disabled button colour, so it greys out exactly like a vanilla one.
        _btnDim = new GUIStyle(_btn);
        _btnDim.normal.background = ValheimTheme.ButtonDimTex
            ?? BorderTex(new Color(0.271f, 0.169f, 0.118f, 1f),
                        new Color(0.443f, 0.353f, 0.243f, 1f), 2);
        _btnDim.hover.background  = _btnDim.normal.background;
        _btnDim.active.background = _btnDim.normal.background;
        _btnDim.normal.textColor  = new Color(text.r * 0.55f, text.g * 0.54f, text.b * 0.5f, 1f);
        _btnDim.hover.textColor   = _btnDim.normal.textColor;

        // Primary action (donate, confirm, OK). Vanilla's own "Craft" button is
        // the SAME wood as every other button — Valheim has no gold slab anywhere
        // in its UI, and painting one made this panel look imported from another
        // game. It is the ordinary button with gold, bold text.
        _btnPrimary = new GUIStyle(_btn) { fontSize = btnTxt + 1, fontStyle = FontStyle.Bold };
        _btnPrimary.alignment = TextAnchor.MiddleCenter;
        _btnPrimary.normal.textColor = gold;
        _btnPrimary.hover.textColor  = Brighten(gold, 1.08f);
        _btnPrimary.active.textColor = gold;

        // Hairline rule between sections: the frame's tan, kept faint.
        _line = new GUIStyle();
        _line.normal.background = SolidTex(new Color(ValheimTheme.Trim.r, ValheimTheme.Trim.g,
                                                     ValheimTheme.Trim.b, 0.28f));

        // Full-screen dim behind a modal. This used to be _line, which is how a
        // colour meant for a 1px rule ended up washed across the entire screen —
        // once the rule took the frame's warm tan, every modal tinted the whole
        // game orange. A scrim's only job is to darken; it is near-black on
        // purpose and shares nothing with the rule.
        _scrim = new GUIStyle();
        _scrim.normal.background = SolidTex(new Color(0.04f, 0.03f, 0.02f, 0.62f));

        _logLine = new GUIStyle(GUI.skin.label) { fontSize = small + 1, wordWrap = true };
        _logLine.normal.textColor = text;

        _label = new GUIStyle(GUI.skin.label) { fontSize = body, wordWrap = true };
        _label.normal.textColor = text;

        // Green "Already Purchased" marker for owned one-time perks.
        _owned = new GUIStyle(GUI.skin.label) { fontSize = small, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight };
        _owned.normal.textColor = ValheimTheme.Green;

        // The donation code, shown big in a dark recessed box.
        _codeBox = new GUIStyle(GUI.skin.box) { fontSize = header + 2, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        _codeBox.normal.background = SolidTex(new Color(0.13f, 0.08f, 0.05f, 1f));
        _codeBox.normal.textColor = gold;
        _codeBox.padding = new RectOffset(8, 8, 10, 10);

        // "Terms of Use" rendered as a link.
        _linkBtn = new GUIStyle(GUI.skin.label) { fontSize = small };
        _linkBtn.normal.textColor = ValheimTheme.Link;
        _linkBtn.hover.textColor = Brighten(ValheimTheme.Link, 1.1f);

        _pillOn = new GUIStyle(GUI.skin.label) { fontSize = small, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight };
        _pillOn.normal.textColor = ValheimTheme.Green;

        _pillOff = new GUIStyle(_pillOn);
        _pillOff.normal.textColor = ValheimTheme.Amber;

        // Shop category header — gold, bold, all-caps look (caller upper-cases),
        // matching the panel titles the game sets in that same gold.
        _catHdr = new GUIStyle(GUI.skin.label) { fontSize = body + 2, fontStyle = FontStyle.Bold };
        _catHdr.normal.textColor = gold;

        // Dim secondary line for auto-derived bundle contents.
        _dim = new GUIStyle(GUI.skin.label) { fontSize = small, wordWrap = true };
        _dim.normal.textColor = dim;

        // Exchange-rate callout on the Donate tab — the one number donors most
        // want before they open their wallet, so it gets the panel's own frame at
        // display size rather than the usual muted helper-text treatment.
        _rateBox = new GUIStyle(GUI.skin.box) { fontSize = header + 4, fontStyle = FontStyle.Bold,
                                                alignment = TextAnchor.MiddleCenter, wordWrap = false };
        // Deliberately NOT the panel sprite. Gold on that wood is gold on a
        // mid-tone, which is the contrast this callout cannot afford to lose --
        // it is the one number a donor is looking for. Dark recess, gold on it,
        // the way the game frames its own stat blocks.
        _rateBox.normal.background = BorderTex(new Color(0.145f, 0.090f, 0.055f, 0.95f),
                                               ValheimTheme.Trim, 3, 24);
        _rateBox.border = new RectOffset(4, 4, 4, 4);
        _rateBox.normal.textColor = gold;
        _rateBox.padding = new RectOffset(10, 10, 12, 6);

        // Caption under the callout (inside the same visual block).
        _rateSub = new GUIStyle(GUI.skin.label) { fontSize = small, alignment = TextAnchor.MiddleCenter, wordWrap = true };
        _rateSub.normal.textColor = dim;

        // Swap every style over to the game's own serif. Headers and buttons take
        // the bold cut when the game has one loaded; everything else takes the
        // body face and synthesizes weight through FontStyle as before.
        var regular = ValheimTheme.Regular;
        var bold    = ValheimTheme.Bold ?? regular;
        if (regular != null)
        {
            foreach (var s in new[] { _sub, _btn, _btnDim, _logLine, _label, _linkBtn,
                                      _pillOn, _pillOff, _owned, _dim, _rateSub })
                s.font = regular;
            foreach (var s in new[] { _hdr, _btnActive, _btnPrimary, _codeBox, _catHdr, _rateBox })
                s.font = bold;
        }

        _styleVersion = ValheimTheme.Version;
        _stylesReady = true;
    }

    // Valheim's own UI text carries a dark outline; that is what lets warm, light
    // text sit on warm, light wood at all. IMGUI has no outline, so the accent
    // styles draw twice — near-black at a one-pixel offset, then the real colour
    // on top. Body text is near-white and reads on the wood unaided, so it does
    // not pay this cost.
    //
    // GUI.contentColor MULTIPLIES the style's colour, so setting it to black
    // blackens whatever the caller had set (DrawResultModal tints its title this
    // way) and the restore hands that tint back for the real pass.
    private static void ShadowLabel(string text, GUIStyle style, params GUILayoutOption[] opts)
    {
        var content = new GUIContent(text);
        var r = GUILayoutUtility.GetRect(content, style, opts);

        var prev = GUI.contentColor;
        GUI.contentColor = new Color(0f, 0f, 0f, 0.8f);
        GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), content, style);
        GUI.contentColor = prev;

        GUI.Label(r, content, style);
    }

    // Wood first, then the frame over it — see the note on _bgFill. The fill is
    // inset a couple of pixels so the frame's own edge always covers it, rather
    // than a hard rectangle showing past a carved or rounded corner.
    private void DrawPanel(Rect r)
    {
        GUI.Box(new Rect(r.x + 2f, r.y + 2f, r.width - 4f, r.height - 4f), GUIContent.none, _bgFill);
        GUI.Box(r, GUIContent.none, _bg);
    }

    private static Color Brighten(Color c, float f)
        => new Color(Mathf.Min(1f, c.r * f), Mathf.Min(1f, c.g * f), Mathf.Min(1f, c.b * f), c.a);

    private static Texture2D SolidTex(Color c)
    {
        var t = new Texture2D(1, 1);
        t.SetPixel(0, 0, c);
        t.Apply();
        return t;
    }

    // A 9-slice-able panel/button texture: a solid fill with a `t`-pixel border,
    // used with GUIStyle.border so the frame stays crisp at any size. This is the
    // fallback path, used when the game's own sprites couldn't be lifted (see
    // ValheimTheme) — the colours passed in are the written-down Valheim palette,
    // so it comes out flat where the real thing has grain, but never the wrong
    // colour.
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


    // ─── render ───────────────────────────────────────────────────────────

    private void OnGUI()
    {
        if (!_open) return;
        if (!_stylesReady || _styleVersion != ValheimTheme.Version) InitStyles();

        if (!ScreenIsClear())
        {
            _open = false; return;
        }

        // Clamp to the screen so a tall panel is never drawn off the top/bottom
        // edge (which would hide the lower tabs' content).
        float pw = Mathf.Min(PanelW, Screen.width - 40);
        float ph = Mathf.Min(PanelH, Screen.height - 40);
        var rect = new Rect((Screen.width - pw) / 2f, (Screen.height - ph) / 2f, pw, ph);
        DrawPanel(rect);

        // A purchase that never got a verdict. This is NOT a failure — the server
        // may have debited and lost the reply — so it is reported as unknown, and
        // the panel refuses to guess which way it went.
        if (_pendingBuySku != null && Time.realtimeSinceStartup > _pendingBuyDeadline)
        {
            _pendingBuySku = null;
            _resultKind = BuyOutcome.Unknown;
            _resultText = "The server never answered. Your Valcoins are most likely untouched, "
                          + "but check your balance before trying again - and if it dropped without "
                          + "the item arriving, tell an admin: every spend is in the ledger and can "
                          + "be put right.";
        }

        // While a modal is up, disable + dim the panel behind it so its buttons
        // can't be clicked through the overlay (IMGUI renders disabled controls
        // greyed, which reads as a native modal backdrop).
        bool modalOpen = _showTerms || _confirmSku != null || _resultText != null
                         || _zoomImage != null || _pendingBuySku != null;
        GUI.enabled = !modalOpen;

        GUILayout.BeginArea(new Rect(rect.x + _contentInset, rect.y + _contentInset,
                                     rect.width - _contentInset * 2, rect.height - _contentInset * 2));

        // Header row: title + live/offline + close.
        GUILayout.BeginHorizontal();
        ShadowLabel("Valheim Donations", _hdr);
        GUILayout.FlexibleSpace();
        ShadowLabel(_online ? "Live" : "Offline", _online ? _pillOn : _pillOff, GUILayout.Width(70), GUILayout.Height(22));
        GUILayout.Space(6);
        if (GUILayout.Button("X", _btn, GUILayout.Width(30))) _open = false;
        GUILayout.EndHorizontal();

        GUILayout.Label($"Balance:  {_balance} Valcoins", _label);
        DrawOwnedCharges();
        DrawQuestProgress();
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
        else if (_pendingBuySku != null) DrawWorkingModal();
        else if (_confirmSku != null) DrawConfirmModal();
    }

    // ─── Purchase in flight ─────────────────────────────────────────────────

    // Shown from the moment "Yes, buy" is pressed until the verdict lands. It
    // exists because the wait can legitimately run to tens of seconds when the
    // server is re-asking a spend it got no answer to, and an un-narrated pause
    // that long reads as a broken button — which is what pushed players into
    // clicking Buy a second time.
    private void DrawWorkingModal()
    {
        GUI.Box(new Rect(0, 0, Screen.width, Screen.height), GUIContent.none, _scrim);

        int w = Mathf.Min(420, Screen.width - 60);
        int h = Mathf.Min(200, Screen.height - 60);
        var r = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
        DrawPanel(r);

        GUILayout.BeginArea(new Rect(r.x + _contentInset, r.y + _contentInset,
                                     r.width - _contentInset * 2, r.height - _contentInset * 2));

        ShadowLabel("Processing Purchase", _hdr);
        DrawHr();
        GUILayout.Space(8);

        float waited = Time.realtimeSinceStartup - _pendingBuyStarted;
        GUILayout.Label("Talking to the Valcoin service...", _label);
        GUILayout.Space(4);
        GUILayout.Label(waited > 6f
            ? "Still waiting. The server retries a purchase it gets no answer to, so this "
              + "can take a moment - don't buy again, you won't be charged twice."
            : "This usually takes about a second.", _sub);

        GUILayout.FlexibleSpace();
        GUILayout.Label($"{Mathf.CeilToInt(waited)}s", _sub);

        GUILayout.EndArea();
    }

    // ─── Purchase result modal ──────────────────────────────────────────────

    private void DrawResultModal()
    {
        GUI.Box(new Rect(0, 0, Screen.width, Screen.height), GUIContent.none, _scrim);

        int w = Mathf.Min(460, Screen.width - 60);
        int h = Mathf.Min(300, Screen.height - 60);
        var r = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
        DrawPanel(r);

        GUILayout.BeginArea(new Rect(r.x + _contentInset, r.y + _contentInset,
                                     r.width - _contentInset * 2, r.height - _contentInset * 2));

        // Green on success, ember-orange on failure, amber on an unknown outcome
        // (the same Live/Offline pill colours used in the header).
        string title;
        Color tint;
        switch (_resultKind)
        {
            case BuyOutcome.Success: title = "Purchase Complete"; tint = new Color(0.5f, 0.85f, 0.45f); break;
            case BuyOutcome.Unknown: title = "Purchase Unconfirmed"; tint = new Color(0.95f, 0.78f, 0.35f); break;
            default:                 title = "Purchase Failed";   tint = new Color(0.95f, 0.55f, 0.3f); break;
        }

        var prev = GUI.contentColor;
        GUI.contentColor = tint;
        ShadowLabel(title, _hdr);
        GUI.contentColor = prev;

        DrawHr();
        GUILayout.Space(8);
        GUILayout.Label(_resultText, _label);

        // A failure is now a promise, not just a report: the server refuses to
        // leave a purchase debited without delivering, and refunds when it can't.
        if (_resultKind == BuyOutcome.Failed)
        {
            GUILayout.Space(6);
            var pc = GUI.contentColor;
            GUI.contentColor = new Color(0.5f, 0.85f, 0.45f);
            GUILayout.Label("No Valcoins were taken.", _label);
            GUI.contentColor = pc;
        }

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
            ShadowLabel($"{ChargeLabel(kv.Key)} x{kv.Value}", _pillOn, GUILayout.ExpandWidth(false));
            GUILayout.Space(10);
        }
        if (any) { GUILayout.FlexibleSpace(); GUILayout.EndHorizontal(); }
    }

    // Daily-quest status, shown under the balance on every tab. This is the
    // surface a player checks *before* asking "why didn't that quest pay?" —
    // the in-the-moment messages QuestFlow sends are deliberately quiet, so
    // something has to answer the question passively.
    private void DrawQuestProgress()
    {
        if (_questCap <= 0) return;   // quests disabled server-side

        var line = $"Daily quests: {_questEarned}/{_questCap}";
        if (!string.IsNullOrEmpty(_questResetsIn)) line += $"  ·  resets in {_questResetsIn}";
        if (_questStreak > 0) line += $"  ·  {_questStreak}-day streak";

        GUILayout.BeginHorizontal();
        ShadowLabel(line, _questEarned >= _questCap ? _pillOn : _sub, GUILayout.ExpandWidth(false));
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
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
        ShadowLabel("Support the server", _hdr);
        GUILayout.Label(
            "Donating is always optional. Playing is free, and every perk is cosmetic " +
            "or a weekly-limited supply - never raw power.", _sub);
        GUILayout.Space(8);
        DrawRateCallout();

        GUILayout.Label("How it works", _label);
        GUILayout.Label("1.  Click \"Get my donation code\" below to generate your personal code.", _label);
        GUILayout.Label("2.  Click \"Open donation portal\" - it opens in your web browser.", _label);
        GUILayout.Label("3.  Pick a provider (Ko-fi, Patreon, or GCash/Maya).", _label);
        GUILayout.Label("4.  On Ko-fi, paste your code into the message box - it can't be filled in " +
                        "for you. GCash/Maya carries the code automatically, and Patreon needs a " +
                        "one-time account link instead.", _label);
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
            _donateWaitingSince = now;
            RpcLayer.SendAction("donate");
        }

        // Give up if the server never answers. The request is fire-and-forget over
        // an RPC with no reply channel of its own, so a server that takes the
        // action but never pushes a panel message would otherwise leave this
        // spinning silently until the player restarts the game.
        if (_donateWaitingSince >= 0f && now - _donateWaitingSince > DonateReplyTimeoutSeconds)
        {
            _donateWaitingSince = -1f;
            _donateCooldownUntil = 0f;   // let them retry straight away
            _donateStatus = "The server didn't answer in time. Press the button again, and "
                          + "if it keeps failing tell an admin to check the server log for "
                          + "[Valcoin] errors — your Valcoins are safe either way.";
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
        ShadowLabel(category.ToUpperInvariant(), _catHdr);

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
            ShadowLabel("Already Purchased", _owned, GUILayout.Width(160));
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
        ShadowLabel("Gift Valcoins", _hdr);
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
        ShadowLabel("Top Patrons", _hdr);
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

        GUI.Box(new Rect(0, 0, Screen.width, Screen.height), GUIContent.none, _scrim);

        if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
        { _zoomImage = null; Event.current.Use(); return; }

        // Fit the texture inside the available area, preserving aspect and never
        // upscaling past 1:1.
        float maxW = Screen.width  * 0.8f;
        float maxH = Screen.height * 0.8f - 70f;   // leave room for caption + button
        float scale = Mathf.Min(maxW / tex.width, maxH / tex.height, 1f);
        float imgW = tex.width * scale, imgH = tex.height * scale;

        float panelW = imgW + _contentInset * 2f;
        float panelH = imgH + _contentInset * 2f + 64f;
        var r = new Rect((Screen.width - panelW) / 2f, (Screen.height - panelH) / 2f, panelW, panelH);

        // Click outside the panel closes. Handled before the panel's own controls
        // so a full-screen hit-target can't swallow the Close button.
        if (Event.current.type == EventType.MouseDown && !r.Contains(Event.current.mousePosition))
        { _zoomImage = null; Event.current.Use(); return; }

        DrawPanel(r);

        GUI.DrawTexture(new Rect(r.x + _contentInset, r.y + _contentInset, imgW, imgH), tex, ScaleMode.ScaleToFit);

        if (!string.IsNullOrEmpty(_zoomCaption))
            GUI.Label(new Rect(r.x + _contentInset, r.y + _contentInset + imgH + 4f, imgW, 24f), _zoomCaption, _rateSub);

        if (GUI.Button(new Rect(r.x + panelW / 2f - 60f, r.y + _contentInset + imgH + 32f, 120f, 32f), "Close", _btn))
            _zoomImage = null;
    }

    // ─── Purchase confirmation modal ────────────────────────────────────────

    private void DrawConfirmModal()
    {
        var sku = _confirmSku;
        if (sku == null) return;

        // Dim backdrop over the whole screen.
        GUI.Box(new Rect(0, 0, Screen.width, Screen.height), GUIContent.none, _scrim);

        bool isCharge = sku.Effect == "add_charges";
        bool isVfx    = sku.Effect == "armor_vfx";

        // A reason this purchase cannot go through, checked on the buyer's own
        // machine BEFORE any coins move. The server can't run this one: an aura
        // binds to whatever armour this client has equipped, which is state only
        // this client holds — so without the check the sale went through, the
        // apply failed here, and the player was left paid-up and empty-handed.
        string blocker = null;
        if (isVfx)
        {
            string vfxSlot = ArmorVfx.SlotFor(sku.Perk);
            if (vfxSlot != null && ArmorVfx.EquippedIn(Player.m_localPlayer, vfxSlot) == null)
                blocker = $"You have no {vfxSlot} armor equipped. Equip a piece first - "
                          + "the familiar binds to it, and buying now would spend Valcoins on nothing.";
        }

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
        if (blocker != null) baseH += 70;
        int h = Mathf.Min(baseH + previewH, Screen.height - 60);
        var r = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
        DrawPanel(r);

        GUILayout.BeginArea(new Rect(r.x + _contentInset, r.y + _contentInset,
                                     r.width - _contentInset * 2, r.height - _contentInset * 2));

        ShadowLabel("Confirm Purchase", _hdr);
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

        if (blocker != null)
        {
            GUILayout.Space(8);
            var bc = GUI.color;
            GUI.color = new Color(1f, 0.6f, 0.4f);
            GUILayout.Label(blocker, _label);
            GUI.color = bc;
        }

        GUILayout.FlexibleSpace();
        GUILayout.BeginHorizontal();
        if (blocker != null)
        {
            // Cheaper to refuse here than to debit, fail to apply on this client
            // and unwind it: the aura binds to a piece only this machine can see.
            GUILayout.Label("Yes, buy", _btnDim, GUILayout.Height(38));
        }
        else if (GUILayout.Button("Yes, buy", _btnPrimary, GUILayout.Height(38)))
        {
            RpcLayer.SendAction("buy:" + sku.Id);
            // Arm the "working on it" modal; the server's verdict replaces it.
            _pendingBuySku = sku.Id;
            _pendingBuyStarted = Time.realtimeSinceStartup;
            _pendingBuyDeadline = _pendingBuyStarted + BuyReplyTimeoutSeconds;
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
        GUI.Box(new Rect(0, 0, Screen.width, Screen.height), GUIContent.none, _scrim);

        int w = Mathf.Min(560, Screen.width - 60);
        int h = Mathf.Min(460, Screen.height - 60);
        var r = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
        DrawPanel(r);

        GUILayout.BeginArea(new Rect(r.x + _contentInset, r.y + _contentInset,
                                     r.width - _contentInset * 2, r.height - _contentInset * 2));

        GUILayout.BeginHorizontal();
        ShadowLabel("Terms of Use - Donations", _hdr);
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
