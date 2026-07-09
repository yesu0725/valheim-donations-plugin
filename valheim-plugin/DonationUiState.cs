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
