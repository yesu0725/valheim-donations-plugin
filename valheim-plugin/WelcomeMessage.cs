using System.Collections;
using UnityEngine;
using HarmonyLib;

// One-shot, low-friction donation advertisement.
//
// Fires N seconds after the local player spawns. Single TopLeft HUD line, then
// done — no follow-up nagging this session.
//
// Operator can disable via valcoin_config.json -> welcome_message_enabled: false.
public class WelcomeBanner : MonoBehaviour
{
    private const float DelaySeconds = 5f;
    private static bool _shownThisSession;

    public static void ResetForNewSession() => _shownThisSession = false;

    public static void Show()
    {
        if (_shownThisSession) return;
        if (!Config.WelcomeEnabled) return;
        var go = new GameObject("ValcoinWelcome");
        go.AddComponent<WelcomeBanner>();
        DontDestroyOnLoad(go);
    }

    private IEnumerator Start()
    {
        // Wait for the local player to fully exist + their HUD.
        while (Player.m_localPlayer == null) yield return null;
        yield return new WaitForSeconds(DelaySeconds);
        if (Player.m_localPlayer == null) yield break;

        var msg = string.IsNullOrEmpty(Config.WelcomeMessage)
            ? $"Press {Config.UiToggleKey ?? "F8"} or {Config.CodexToggleKey ?? "F4"} to support the server"
            : Config.WelcomeMessage;
        Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft, msg);
        _shownThisSession = true;
        Destroy(gameObject);
    }
}

// Triggers WelcomeBanner.Show() once per spawn cycle.
[HarmonyPatch(typeof(Player), "OnSpawned")]
public static class WelcomeOnSpawn
{
    static void Postfix(Player __instance)
    {
        if (ZNet.instance == null) return;
        if (ZNet.instance.IsServer() && ZNet.instance.IsDedicated()) return;
        if (__instance != Player.m_localPlayer) return;
        WelcomeBanner.Show();
    }
}
