using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// The panel's skin, lifted at runtime from Valheim's OWN player inventory screen.
//
// WHY THIS EXISTS. Everything in DonationPanel is Unity IMGUI, and IMGUI cannot
// draw a UnityEngine.UI Sprite. So the panel used to APPROXIMATE the game's look
// with hand-mixed colours and a procedurally generated border texture: a dark
// brown fill, a bronze frame, guessed font sizes. It read as "Valheim-ish" and
// nothing more -- the wrong wood, the wrong frame, text a size or two off, and it
// drifted further every time the game retouched its UI.
//
// This class stops guessing. It reads InventoryGui -- the player inventory panel
// and its "Take All" button -- and pulls out:
//
//   * the panel background sprite and its 9-slice border,
//   * the button sprite in each of its four states (normal / hover / pressed /
//     disabled), honouring whichever transition the button actually uses,
//   * the serif font, the text colours, and the font SIZES the inventory itself
//     renders at, scaled by the live canvas scale factor so our pixels match the
//     game's pixels at any resolution or GUI-scale setting.
//
// Sprites live in a non-readable atlas, so each one is copied through a
// RenderTexture blit (the standard way to read a texture the CPU cannot touch)
// into a small readable Texture2D that GUIStyle.normal.background accepts. The
// sprite's own border is carried across to GUIStyle.border, so 9-slicing stays
// crisp at any size and the frame corners never stretch.
//
// EVERY PIECE IS INDEPENDENTLY OPTIONAL. Each extraction is wrapped on its own,
// and DonationPanel falls back to its old hand-drawn look for any field that
// comes back null. A future Valheim build that renames or restructures the
// inventory therefore costs us the theme, never the panel.
//
// Client-only: InventoryGui does not exist on a dedicated server.
public static class ValheimTheme
{
    // True once an extraction pass has run. Individual fields may still be null;
    // check them, not just this.
    public static bool Ready { get; private set; }

    // Bumped on every successful (re)extraction. DonationPanel rebuilds its
    // GUIStyles when this changes, which is how a mid-session resolution or
    // GUI-scale change re-themes the panel without a restart.
    public static int Version { get; private set; }

    // --- skins ------------------------------------------------------------
    public static Texture2D PanelTex;
    public static RectOffset PanelBorder;
    public static Texture2D ButtonTex, ButtonHoverTex, ButtonActiveTex, ButtonDimTex;
    public static RectOffset ButtonBorder;

    // --- typography -------------------------------------------------------
    public static Font Regular, Bold;
    public static int BodySize   = 15;
    public static int SmallSize  = 13;
    public static int HeaderSize = 20;
    public static int ButtonSize = 15;

    // --- colours ----------------------------------------------------------
    //
    // FIXED, NOT EXTRACTED. These are sampled from Valheim's crafting/inventory
    // panel: warm brown wood, a tan carved frame, gold headings, cream body text.
    //
    // An earlier cut read them off whatever TMP_Text the inventory happened to
    // expose and brightened the results to make buttons "pop". Both were
    // mistakes. The brightening blew warm browns out into orange, and reading a
    // colour off an arbitrary label meant one odd label re-tinted the whole
    // panel — together they turned it red. The game's palette is a known, stable
    // thing; there is nothing to discover about it at runtime, so it is written
    // down. SIZES are still measured from the live UI, because those genuinely do
    // vary with the player's resolution and GUI-scale setting.
    public static readonly Color Wood      = new Color(0.478f, 0.294f, 0.200f, 0.980f); // panel fill
    public static readonly Color WoodDark  = new Color(0.369f, 0.224f, 0.153f, 1f);     // button fill
    public static readonly Color WoodLight = new Color(0.557f, 0.380f, 0.267f, 1f);     // hover / selected
    public static readonly Color Trim      = new Color(0.757f, 0.584f, 0.369f, 1f);     // carved frame
    public static readonly Color Gold      = new Color(0.965f, 0.745f, 0.322f, 1f);     // headings
    public static readonly Color Cream     = new Color(0.973f, 0.957f, 0.925f, 1f);     // body text
    public static readonly Color CreamDim  = new Color(0.898f, 0.863f, 0.796f, 1f);     // secondary
    public static readonly Color Amber     = new Color(0.988f, 0.780f, 0.478f, 1f);     // warnings
    public static readonly Color Green     = new Color(0.663f, 0.886f, 0.588f, 1f);     // owned / held
    public static readonly Color Link      = new Color(0.639f, 0.827f, 1.000f, 1f);     // hyperlink

    // WHY EVERY ONE OF THESE IS LIGHT. The wood is a MID-TONE, and against a
    // mid-tone only near-white or near-black has any contrast. The first cut used
    // the reference screenshot's colours literally -- a muted tan for secondary
    // text, a saturated amber for headings -- and both landed within a hair of
    // the wood's own luminance: the daily-quest line and the rate caption were
    // invisible, the headings barely there. The screenshot gets away with those
    // colours because the game puts its text on a DARKER ground and outlines it.
    // So: body and secondary text are near-white, and the warm accents are
    // brighter than the game's AND drawn with a dark shadow behind them
    // (DonationPanel.ShadowLabel) or on a dark ground (the code and rate boxes).
    // Sampling a palette is not the same as reading it against its background.

    public static Color TextColor       = Cream;
    public static Color DimColor        = CreamDim;
    public static Color HeaderColor     = Gold;
    public static Color ButtonTextColor = Cream;

    // Canvas scale factor at extraction time. IMGUI works in screen pixels while
    // the game's UI works in canvas units, so every size read from a TMP_Text is
    // multiplied by this to land on screen at the same height as the game's own.
    public static float Scale = 1f;

    private static float _nextAttempt;
    private static readonly List<Texture2D> _owned = new List<Texture2D>();

    // Cheap to call every frame: it throttles itself, and after a successful pass
    // it only re-extracts when the canvas scale actually changes.
    public static void Ensure()
    {
        if (Time.realtimeSinceStartup < _nextAttempt) return;
        _nextAttempt = Time.realtimeSinceStartup + 1f;

        var gui = InventoryGui.instance;
        if (gui == null || gui.m_player == null) return;

        float scale = ReadScale(gui.m_player);
        if (Ready && Mathf.Approximately(scale, Scale)) return;

        try
        {
            Build(gui, scale);
        }
        catch (Exception ex)
        {
            // Do not hammer a hierarchy we cannot read; the panel looks fine without us.
            _nextAttempt = Time.realtimeSinceStartup + 15f;
            Debug.LogWarning("[Valcoin] theme extraction failed, using the built-in skin: " + ex.Message);
        }
    }

    private static void Build(InventoryGui gui, float scale)
    {
        Discard();
        Scale = scale;

        TryStep("fonts",  ReadFonts);
        TryStep("text",   () => ReadTextMetrics(gui));
        if (Config.PanelUseGameSkin)
        {
            TryStep("panel",  () => ReadPanel(gui));
            TryStep("button", () => ReadButton(gui));
        }

        Ready = true;
        Version++;
        Debug.Log($"[Valcoin] theme from InventoryGui: scale {Scale:0.##}, body {BodySize}px, "
                  + $"header {HeaderSize}px, button {ButtonSize}px, "
                  + $"panel {(PanelTex != null ? "yes" : "no")}, "
                  + $"button skin {(ButtonTex != null ? "yes" : "no")}, "
                  + $"font {(Regular != null ? Regular.name : "default")}");
    }

    private static void TryStep(string what, Action step)
    {
        try { step(); }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Valcoin] theme: couldn't read the {what} ({ex.Message}); keeping the default.");
        }
    }

    // --- canvas scale -----------------------------------------------------

    // Walked by hand rather than GetComponentInParent: the inventory hierarchy is
    // inactive whenever the screen is closed, and the inactive-inclusive overload
    // is not available on every Unity version Valheim has shipped.
    private static float ReadScale(Transform t)
    {
        while (t != null)
        {
            var c = t.GetComponent<Canvas>();
            if (c != null)
            {
                var root = c.rootCanvas != null ? c.rootCanvas : c;
                float s = root.scaleFactor;
                if (s > 0.01f) return Mathf.Clamp(s, 0.5f, 3f);
            }
            t = t.parent;
        }
        return 1f;
    }

    // --- fonts ------------------------------------------------------------

    // IMGUI needs a legacy Font; the game's UI runs on TMP font ASSETS, which
    // IMGUI cannot use. Valheim keeps the legacy Averia faces loaded alongside
    // them, so match by name among every loaded font.
    private static void ReadFonts()
    {
        var fonts = Resources.FindObjectsOfTypeAll<Font>();
        Regular = PickFont(fonts, "AveriaSerifLibre-Regular", "AveriaSerifLibre", "Averia", "Norse");
        Bold    = PickFont(fonts, "AveriaSerifLibre-Bold", "Averia Serif Libre Bold", "Norsebold");
        // A "bold" that resolved to the same asset as the body face is no bold at
        // all; drop it so callers synthesize weight via FontStyle instead.
        if (Bold != null && Bold == Regular) Bold = null;
    }

    // Matches are substring matches, so "AveriaSerifLibre-Bold" also matches
    // "AveriaSerifLibre-BoldItalic" -- which is how the headers, the primary
    // button and the rate callout all came out slanted while the body text
    // stayed upright. Slanted faces are excluded outright; nothing in this panel
    // wants a real italic cut, and the two styles that read as italic ask for it
    // through FontStyle.
    private static Font PickFont(Font[] fonts, params string[] preferences)
    {
        foreach (var pref in preferences)
            foreach (var f in fonts)
                if (f != null && !string.IsNullOrEmpty(f.name)
                    && f.name.IndexOf(pref, StringComparison.OrdinalIgnoreCase) >= 0
                    && f.name.IndexOf("italic", StringComparison.OrdinalIgnoreCase) < 0
                    && f.name.IndexOf("oblique", StringComparison.OrdinalIgnoreCase) < 0)
                    return f;
        return null;
    }

    // --- panel background -------------------------------------------------

    // The inventory's own frame is the largest 9-sliced Image in its hierarchy;
    // everything smaller is a slot, an icon backdrop or a widget frame.
    private static void ReadPanel(InventoryGui gui)
    {
        Image best = null;
        float bestArea = 0f;
        foreach (var img in gui.m_player.GetComponentsInChildren<Image>(true))
        {
            var sprite = img.sprite;
            if (sprite == null || sprite.border == Vector4.zero) continue;
            if (img.type != Image.Type.Sliced && img.type != Image.Type.Tiled) continue;

            var r = img.rectTransform.rect;
            float area = Mathf.Abs(r.width * r.height);
            if (area < 40000f) continue;              // smaller than ~200x200: a widget, not the panel
            if (area > bestArea) { bestArea = area; best = img; }
        }
        if (best == null) return;

        var tex = FromSprite(best.sprite, best.color);
        if (!Plausible(tex, "panel")) { UnityEngine.Object.Destroy(tex); return; }

        PanelTex = Own(tex);
        PanelBorder = BorderOf(best.sprite);
    }

    // A sanity check on anything lifted out of the game, because the failure mode
    // of getting it wrong is a panel that is loud and wrong rather than a panel
    // that is obviously broken — and nobody notices a colour cast from reading
    // the code. A skin that comes back washed out, red-hot or nearly invisible is
    // rejected outright, and DonationPanel falls back to the written-down palette.
    private static bool Plausible(Texture2D tex, string what)
    {
        if (tex == null) return false;
        var px = tex.GetPixels();
        if (px.Length == 0) return false;

        int step = Mathf.Max(1, px.Length / 4096);
        float r = 0f, g = 0f, b = 0f, a = 0f; int n = 0;
        for (int i = 0; i < px.Length; i += step) { r += px[i].r; g += px[i].g; b += px[i].b; a += px[i].a; n++; }
        r /= n; g /= n; b /= n; a /= n;

        string reject = null;
        if (a < 0.15f) reject = $"almost fully transparent (alpha {a:0.00})";
        else if (0.299f * r + 0.587f * g + 0.114f * b > 0.70f) reject = "washed out";
        else if (r - b > 0.45f) reject = $"a red cast (r {r:0.00} vs b {b:0.00})";

        if (reject == null) return true;
        Debug.LogWarning($"[Valcoin] theme: the extracted {what} has {reject}; using the built-in palette instead.");
        return false;
    }

    // --- button skin ------------------------------------------------------

    // "Take All" is the button InventoryMenuButton already clones for the
    // inventory-screen entry point, so the panel's buttons and that entry point
    // are guaranteed to be the same button.
    private static void ReadButton(InventoryGui gui)
    {
        var btn = gui.m_takeAllButton;
        if (btn == null) return;

        var img = (btn.targetGraphic as Image) ?? btn.GetComponent<Image>();
        if (img == null || img.sprite == null) return;

        var baseTex = FromSprite(img.sprite, img.color);
        if (!Plausible(baseTex, "button")) { UnityEngine.Object.Destroy(baseTex); return; }

        ButtonBorder = BorderOf(img.sprite);

        if (btn.transition == Selectable.Transition.SpriteSwap)
        {
            var st = btn.spriteState;
            ButtonTex       = Own(baseTex);
            ButtonHoverTex  = Own(st.highlightedSprite != null ? FromSprite(st.highlightedSprite, img.color) : Tinted(baseTex, 1.22f, 1f));
            ButtonActiveTex = Own(st.pressedSprite     != null ? FromSprite(st.pressedSprite,     img.color) : Tinted(baseTex, 0.82f, 1f));
            ButtonDimTex    = Own(st.disabledSprite    != null ? FromSprite(st.disabledSprite,    img.color) : Tinted(baseTex, 0.58f, 0.85f));
        }
        else
        {
            // ColorTint (Valheim's usual choice) or None: the button is one sprite
            // multiplied by a per-state colour, so reproduce exactly that.
            var c = btn.colors;
            float m = Mathf.Max(0.01f, c.colorMultiplier);
            ButtonTex       = Own(Multiplied(baseTex, c.normalColor      * m));
            ButtonHoverTex  = Own(Multiplied(baseTex, c.highlightedColor * m));
            ButtonActiveTex = Own(Multiplied(baseTex, c.pressedColor     * m));
            ButtonDimTex    = Own(Multiplied(baseTex, c.disabledColor    * m));
            UnityEngine.Object.Destroy(baseTex);
        }
    }

    // --- type sizes and colours -------------------------------------------

    // Read from the inventory's own labels rather than chosen: the body size is
    // whichever size the screen uses MOST (slot counts and stat lines), and the
    // header size is its largest label. Both come back in canvas units and are
    // converted to screen pixels here.
    private static void ReadTextMetrics(InventoryGui gui)
    {
        var counts = new Dictionary<int, int>();
        TMP_Text biggest = null;

        var texts = gui.m_player.GetComponentsInChildren<TMP_Text>(true);
        foreach (var t in texts)
        {
            if (t == null) continue;
            int size = Mathf.RoundToInt(t.fontSize);
            if (size < 8 || size > 44) continue;       // auto-sized outliers
            counts.TryGetValue(size, out var n);
            counts[size] = n + 1;
            if (biggest == null || size > Mathf.RoundToInt(biggest.fontSize)) biggest = t;
        }

        int commonSize = 0, commonCount = 0;
        foreach (var kv in counts)
            if (kv.Value > commonCount || (kv.Value == commonCount && kv.Key > commonSize))
            { commonSize = kv.Key; commonCount = kv.Value; }

        if (commonSize > 0)
        {
            BodySize  = Px(commonSize);
            SmallSize = Mathf.Max(11, BodySize - 2);
        }

        if (biggest != null)
            HeaderSize = Mathf.Max(BodySize + 3, Px(Mathf.RoundToInt(biggest.fontSize)));

        var label = gui.m_takeAllButton != null
            ? gui.m_takeAllButton.GetComponentInChildren<TMP_Text>(true)
            : null;
        if (label != null)
        {
            int s = Mathf.RoundToInt(label.fontSize);
            if (s >= 8 && s <= 44) ButtonSize = Px(s);
        }
    }

    private static int Px(int canvasUnits) => Mathf.Clamp(Mathf.RoundToInt(canvasUnits * Scale), 11, 40);

    // --- sprite -> readable Texture2D -------------------------------------

    // UI atlases are not CPU-readable, so the sprite is copied on the GPU into a
    // temporary RenderTexture and read back from there. `tint` folds the Image's
    // own colour into the pixels, since IMGUI draws the background texture
    // untinted.
    private static Texture2D FromSprite(Sprite sprite, Color tint)
    {
        var src = sprite.texture;
        var area = sprite.textureRect;
        int w = Mathf.Max(1, Mathf.RoundToInt(area.width));
        int h = Mathf.Max(1, Mathf.RoundToInt(area.height));

        // Default, NOT Linear. Valheim renders in linear colour space, so sampling
        // an sRGB atlas converts to linear on read; writing into a Linear target
        // then stores those linear values raw, and IMGUI later decodes them AGAIN
        // as sRGB when it draws — a double conversion that shifts every colour.
        // Default resolves to an sRGB target in a linear project, which re-encodes
        // on write and round-trips the sprite back to the bytes it started as.
        var rt = RenderTexture.GetTemporary(src.width, src.height, 0,
                                            RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
        var prev = RenderTexture.active;
        try
        {
            Graphics.Blit(src, rt);
            RenderTexture.active = rt;

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(area.x, area.y, w, h), 0, 0);
            if (tint != Color.white)
            {
                var px = tex.GetPixels();
                for (int i = 0; i < px.Length; i++) px[i] *= tint;
                tex.SetPixels(px);
            }
            tex.Apply();
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            return tex;
        }
        finally
        {
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
        }
    }

    // Sprite.border is (left, bottom, right, top); GUIStyle.border is
    // (left, right, top, bottom). Getting this mapping wrong is what turns a
    // 9-slice into a smeared corner, so it lives in exactly one place.
    private static RectOffset BorderOf(Sprite sprite)
    {
        var b = sprite.border;
        return new RectOffset(Mathf.RoundToInt(b.x), Mathf.RoundToInt(b.z),
                              Mathf.RoundToInt(b.w), Mathf.RoundToInt(b.y));
    }

    private static Texture2D Multiplied(Texture2D src, Color tint)
    {
        var copy = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
        var px = src.GetPixels();
        for (int i = 0; i < px.Length; i++) px[i] *= tint;
        copy.SetPixels(px);
        copy.Apply();
        copy.wrapMode = TextureWrapMode.Clamp;
        copy.filterMode = FilterMode.Bilinear;
        return copy;
    }

    private static Texture2D Tinted(Texture2D src, float brightness, float alpha)
        => Multiplied(src, new Color(brightness, brightness, brightness, alpha));

    // A brightened or darkened variant of the game's button, for the states
    // Valheim's own button does not have: the selected tab and the primary action.
    public static Texture2D ButtonVariant(Color tint)
        => ButtonTex != null ? Own(Multiplied(ButtonTex, tint)) : null;

    private static Texture2D Own(Texture2D t)
    {
        if (t != null) _owned.Add(t);
        return t;
    }

    // Re-extraction allocates a fresh set; the previous one has to go, or a player
    // who resizes their window repeatedly leaks a texture set per resize.
    private static void Discard()
    {
        foreach (var t in _owned)
            if (t != null) UnityEngine.Object.Destroy(t);
        _owned.Clear();
        PanelTex = ButtonTex = ButtonHoverTex = ButtonActiveTex = ButtonDimTex = null;
        PanelBorder = null;
        ButtonBorder = null;
    }
}
