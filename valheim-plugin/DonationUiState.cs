using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

// Shared "is a donation UI panel open?" state + the input-suppression patches
// that make the F4 Codex and F8 panel usable with the mouse.
//
// The panels themselves release the cursor each frame while open
// (Cursor.lockState = None, Cursor.visible = true, GameCamera.m_mouseCapture =
// false). These two patches stop the click/movement from ALSO reaching the
// game (swinging your weapon, rotating the camera) — the same approach
// ServerGuide's Codex uses:
//   * Player.TakeInput gates attack / use / interact.
//   * PlayerController.TakeInput(bool) gates movement + mouse-look.
public static class DonationUiState
{
    public static bool PanelOpen;
    public static bool AnyOpen => PanelOpen;

    // GameCamera.m_mouseCapture is non-public in the raw assembly this plugin
    // references, so we poke it via cached reflection. Setting it false makes
    // GameCamera.UpdateMouseCapture take its "release the cursor" branch.
    private static FieldInfo _mouseCapture;

    public static void SetMouseCapture(bool value)
    {
        var cam = GameCamera.instance;
        if (cam == null) return;
        if (_mouseCapture == null)
            _mouseCapture = typeof(GameCamera).GetField(
                "m_mouseCapture", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        _mouseCapture?.SetValue(cam, value);
    }
}

// String-bound (not nameof) because TakeInput is non-public in the raw
// assembly_valheim this plugin references; Harmony resolves it by reflection.
[HarmonyPatch(typeof(Player), "TakeInput")]
internal static class DonationPlayerTakeInputPatch
{
    private static void Postfix(ref bool __result)
    {
        if (DonationUiState.AnyOpen) __result = false;
    }
}

[HarmonyPatch(typeof(PlayerController), "TakeInput", new[] { typeof(bool) })]
internal static class DonationPlayerControllerTakeInputPatch
{
    private static void Postfix(ref bool __result)
    {
        if (DonationUiState.AnyOpen) __result = false;
    }
}

// Runtime handle to the game's ZInput type. It isn't referenceable at compile
// time (internal/absent in the stripped assembly this plugin builds against),
// so we resolve it by name at load and reuse it for every ZInput patch below.
internal static class ZInputRef
{
    public static readonly System.Type Type = AccessTools.TypeByName("ZInput");
}

// The two TakeInput patches above gate movement / attack / interact, but the
// menu + hotkey layer (Inventory, Map, hotbar slots, etc.) is read separately
// through ZInput.GetButton*, and camera zoom through ZInput.GetMouseScrollWheel.
// Without this, typing an amount into a text field while the panel is open can
// trip a bound key ('I', 'M', ...) and open inventory/map (which then closes
// the panel), and the scroll wheel still zooms the world camera. These patches
// swallow those ZInput reads while any donation panel is open, giving the panel
// exclusive keyboard/scroll focus.
//
// Safe because the panel's own toggle uses raw UnityEngine.Input (not ZInput),
// its text fields use IMGUI events, and its scroll views use IMGUI wheel events
// — none of which go through ZInput. Targets are resolved reflectively so a
// renamed/missing overload is skipped rather than crashing Harmony's PatchAll.

// Boolean input reads. We patch EVERY overload of every button/key read on
// ZInput rather than hand-picking signatures: this game build turned out to
// route the Map/Inventory hotkeys through reads that don't match the classic
// GetButtonDown(string) shape (patching only those left 'M' still opening the
// map mid-typing), so enumerate by name and let Harmony take them all.
[HarmonyPatch]
internal static class DonationZInputButtonPatch
{
    private static readonly HashSet<string> BoolReads = new HashSet<string>
    {
        "GetButton", "GetButtonDown", "GetButtonUp",
        "GetKey", "GetKeyDown", "GetKeyUp",
        "GetMouseButton", "GetMouseButtonDown", "GetMouseButtonUp",
    };

    static IEnumerable<MethodBase> TargetMethods()
    {
        var z = ZInputRef.Type;
        var patched = new List<string>();
        if (z != null)
        {
            foreach (var m in AccessTools.GetDeclaredMethods(z))
            {
                if (!m.IsStatic || m.ReturnType != typeof(bool)) continue;
                if (!BoolReads.Contains(m.Name)) continue;
                patched.Add($"{m.Name}/{m.GetParameters().Length}");
                yield return m;
            }
        }
        UnityEngine.Debug.Log("[Valcoin] ZInput input-block: patched " + patched.Count
                              + " method(s): " + string.Join(", ", patched)
                              + $" (ZInput resolved: {z != null}).");
    }

    private static void Postfix(ref bool __result)
    {
        if (DonationUiState.AnyOpen) __result = false;
    }
}

// Hard backstops for the two windows players kept tripping while typing: even
// if some future input path slips past the ZInput patches, the map and the
// inventory simply refuse to open while a donation panel is up. (The panel
// closes itself when either is VISIBLE — see DonationPanel.OnGUI — so without
// these, one stray keypress both opens the map and closes the panel.)
[HarmonyPatch]
internal static class DonationMinimapGuardPatch
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var m in typeof(Minimap).GetMethods(AccessTools.all))
            if (m.Name == "SetMapMode") yield return m;
    }

    private static bool Prefix() => !DonationUiState.AnyOpen;
}

[HarmonyPatch]
internal static class DonationInventoryGuardPatch
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var m in typeof(InventoryGui).GetMethods(AccessTools.all))
            if (m.Name == "Show") yield return m;
    }

    private static bool Prefix() => !DonationUiState.AnyOpen;
}

// Scroll-wheel read used by GameCamera zoom: GetMouseScrollWheel() -> float.
[HarmonyPatch]
internal static class DonationZInputScrollPatch
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        var z = ZInputRef.Type;
        if (z == null) yield break;
        var m = AccessTools.Method(z, "GetMouseScrollWheel");
        if (m != null) yield return m;
    }

    private static void Postfix(ref float __result)
    {
        if (DonationUiState.AnyOpen) __result = 0f;
    }
}
