using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// A "Donations" button on the player inventory screen, sitting directly beneath
// Lost Scrolls II's "Rankings" button.
//
// WHY THIS EXISTS. The donation panel was reachable only by hotkey (F4). A key
// nobody told you about is not discoverable -- a player who never reads the
// welcome message or the wiki has no way to find out the panel exists, and the
// panel is the ONLY input path into the whole system (there are no chat or
// console commands; see docs/SHOP.md). The inventory screen is where a player
// already goes looking for things, so that is where the entry point belongs.
//
// BUILT FROM VANILLA. The button is a CLONE of the inventory's own "Take All"
// button, so it inherits vanilla styling with no authored assets -- the same
// technique Lost Scrolls II uses for its own row, which is what lets the two
// mods' buttons read as one menu instead of two.
//
// WHERE IT SITS. Lost Scrolls II builds a centred row across the top of the
// inventory (Rankings | Tournaments | Bounty Board), positioned from its own
// configurable offset. Rather than duplicate that arithmetic -- which would drift
// the moment an operator nudges that offset, or that mod adds a fourth entry --
// this reads the live Rankings button's own anchors and places itself one row
// below. If Lost Scrolls II isn't installed there is no row to hang off, and the
// button falls back to standing alone at the top centre.
//
// REBUILT PER WORLD LOAD. InventoryGui is destroyed and rebuilt on every world
// load, taking the clone with it, so the button is re-validated on every Show
// rather than created once and cached.
[HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Show))]
public static class InventoryMenuButton
{
    // GameObject name; also the "already built?" probe.
    private const string ButtonName = "VC_DonationsButton";
    private const string ButtonLabel = "Donations";

    // The Lost Scrolls II button to hang off. Matching its name is the whole
    // integration -- no assembly reference and no soft dependency, so nothing
    // breaks if that mod is absent, disabled, or updated.
    internal const string RankingsButtonName = "LSII_RankingsButton";

    // Fallback geometry, used only when the Rankings button isn't there to copy.
    // Deliberately the same numbers Lost Scrolls II uses, so a client running both
    // still lines up during any frame before that row exists.
    internal const float ButtonWidth = 132f;
    internal const float ButtonHeight = 32f;
    internal const float RowGap = 6f;
    internal const float TopEdgeInset = -18f;

    public static void Postfix(InventoryGui __instance)
    {
        try { Ensure(__instance); }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Valcoin] inventory donations button failed: {e.Message}");
        }
    }

    private static void Ensure(InventoryGui gui)
    {
        if (!Config.InventoryButtonEnabled) return;
        if (gui == null || gui.m_player == null) return;

        var source = gui.m_takeAllButton;
        if (source == null) return;

        // Parented to the screen root (the container the inventory panels sit in)
        // rather than to any one panel, so our coordinates share a frame of
        // reference with the Lost Scrolls II row and "one row below" means what it
        // says. It also keeps the button still when a panel is resized.
        var parent = (gui.m_player.parent as RectTransform) ?? gui.m_player;
        if (parent == null) return;

        if (parent.Find(ButtonName) != null) return;   // survived from a previous Show

        Build(source, parent);
        Debug.Log($"[Valcoin] inventory donations button built under '{parent.name}'.");
    }

    private static void Build(Button source, RectTransform parent)
    {
        var clone = Object.Instantiate(source.gameObject, parent);
        clone.name = ButtonName;

        // Opt out of layout. This container drives a layout group, which would
        // otherwise override anchoredPosition every frame -- the button's position
        // has to be ours deliberately, not a side effect of that group happening to
        // centre things.
        var layoutElement = clone.GetComponent<LayoutElement>() ?? clone.AddComponent<LayoutElement>();
        layoutElement.ignoreLayout = true;

        var rt = clone.GetComponent<RectTransform>();
        if (rt != null)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(ButtonWidth, ButtonHeight);
            rt.localScale = Vector3.one;
            rt.anchoredPosition = new Vector2(0f, TopEdgeInset);
        }

        var txt = clone.GetComponentInChildren<TMP_Text>();
        if (txt != null)
        {
            txt.text = ButtonLabel;
            txt.enableAutoSizing = false;
            txt.fontSize = 14f;
        }

        var btn = clone.GetComponent<Button>();
        if (btn != null)
        {
            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(() =>
            {
                // Close the inventory first: the donation panel is full-screen IMGUI
                // and refuses to draw while the inventory is up (DonationPanel.OnGUI),
                // so opening it on top would only close it again. Hide() isn't
                // instant, which is why the open is a deferred REQUEST rather than a
                // direct call -- the panel takes it once the screen is clear.
                if (InventoryGui.instance != null) InventoryGui.instance.Hide();
                DonationPanel.RequestOpen();
            });
            btn.interactable = true;
        }

        // Keeps the button parked under Rankings for as long as it is on screen;
        // see the class comment on DonationButtonAnchor for why placing it once at
        // build time isn't enough.
        clone.AddComponent<DonationButtonAnchor>();

        clone.SetActive(true);
    }
}

// Re-parks the donations button under the Lost Scrolls II "Rankings" button on
// every frame the inventory is on screen.
//
// WHY EVERY FRAME AND NOT ONCE. Both mods add their buttons from a Postfix on the
// same InventoryGui.Show call, and nothing defines which of the two Harmony
// postfixes runs first. Placing ourselves once, at build time, would leave the
// button in the fallback position for a whole inventory session on any load where
// we happened to go first -- overlapping "Tournaments", which is also centred.
// Re-reading the anchor each frame sidesteps the ordering question entirely, and
// picks up a live offset change (that row's offset is operator-configurable) free.
//
// The cost is one Transform.Find, cached on success, and only while the inventory
// is open: InventoryGui deactivates this whole hierarchy when it hides, which
// stops LateUpdate from ticking at all.
public class DonationButtonAnchor : MonoBehaviour
{
    private RectTransform _self;
    private RectTransform _rankings;

    private void Awake() => _self = GetComponent<RectTransform>();

    private void LateUpdate()
    {
        if (_self == null) return;

        // Cached until it dies with its InventoryGui -- or until Lost Scrolls II
        // heals a clone that failed to build, in which case the next Find picks up
        // the replacement.
        if (_rankings == null && _self.parent != null)
            _rankings = _self.parent.Find(InventoryMenuButton.RankingsButtonName) as RectTransform;

        if (_rankings == null)
        {
            // No row to hang off (Lost Scrolls II absent, or its row not built yet):
            // stand alone at the top centre.
            Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                  new Vector2(InventoryMenuButton.ButtonWidth, InventoryMenuButton.ButtonHeight),
                  new Vector2(0f, InventoryMenuButton.TopEdgeInset));
            return;
        }

        // Copy the Rankings button's anchoring and size verbatim, then drop down by
        // exactly one button plus the row gap. Copying rather than assuming means
        // the offset stays correct whatever anchor/pivot convention that row uses.
        var size = _rankings.sizeDelta;
        Place(_rankings.anchorMin, _rankings.anchorMax, _rankings.pivot, size,
              _rankings.anchoredPosition - new Vector2(0f, size.y + InventoryMenuButton.RowGap));
    }

    private void Place(Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 size, Vector2 pos)
    {
        _self.anchorMin = anchorMin;
        _self.anchorMax = anchorMax;
        _self.pivot = pivot;
        _self.sizeDelta = size;
        _self.anchoredPosition = pos;
    }
}
