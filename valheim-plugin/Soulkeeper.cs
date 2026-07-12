using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

// Soulkeeper Charm — a consumable "death insurance" charge (Phase 1).
//
// On the LOCAL player's death, if a charge is available it's consumed and the
// vanilla skill drain is skipped (you keep your skills). It never helps you win
// a fight — it only softens the death tax after you've already lost.
//
// Charges are backend-authoritative (bought via the shop, credited by /api/spend).
// This client caches its own count so the *synchronous* death path can decide
// instantly, then tells the backend to decrement — reconciling on the next poll.
// It's the local player's own state, so there's no "other players' state" gap.
public static class SoulkeeperState
{
    public const string Kind = "soulkeeper";

    public static string LocalSteam64;
    public static int    LocalCharges;
    private static bool  _wardThisDeath;

    public static void UpdateFromState(string steam64, int charges)
    {
        if (!string.IsNullOrEmpty(steam64)) LocalSteam64 = steam64;
        LocalCharges = charges;
    }

    // Local player's death: consume a charge if we have one and arm the skip.
    public static bool TryWardDeath()
    {
        if (LocalCharges <= 0 || string.IsNullOrEmpty(LocalSteam64) || !Config.Ready)
            return false;

        LocalCharges--;                 // optimistic; reconciles on next state poll
        _wardThisDeath = true;
        if (SharedCoroutineRunner.Instance != null)
            SharedCoroutineRunner.Instance.StartCoroutine(ConsumeOnBackend());
        Debug.Log("[Valcoin] Soulkeeper: death warded — skills preserved.");
        return true;
    }

    // True (and clears) if the current death was warded — the skill-loss patch
    // uses this to skip the drain exactly once, within the OnDeath call.
    public static bool ConsumeWardFlag()
    {
        if (!_wardThisDeath) return false;
        _wardThisDeath = false;
        return true;
    }

    private static IEnumerator ConsumeOnBackend()
    {
        var body = new { steam64 = LocalSteam64, kind = Kind };
        yield return BackendClient.Post<ConsumeResp>("/api/charges/consume", body,
            (ok, r, err) =>
            {
                if (!ok || r == null)
                    Debug.LogWarning("[Valcoin] Soulkeeper consume failed (reconciles later): "
                                     + (err ?? "no response"));
                else
                    LocalCharges = r.remaining;   // authoritative count
            });
    }

    private class ConsumeResp { public bool consumed; public int remaining; }
}

// Keeps the local charge cache fresh even when the panel is closed, so a death
// is warded regardless of whether the player opened the shop recently.
public class SoulkeeperPoller : MonoBehaviour
{
    private const float IntervalSeconds = 45f;

    private void Start() => StartCoroutine(Loop());

    private IEnumerator Loop()
    {
        while (true)
        {
            if (Config.Ready && !(ZNet.instance != null && ZNet.instance.IsServer()))
            {
                var steam64 = LocalIdentity.Steam64();
                if (!string.IsNullOrEmpty(steam64))
                    yield return BackendClient.Get<ChargesResp>(
                        $"/api/state/{steam64}",
                        (ok, r, err) =>
                        {
                            if (!ok || r == null) return;
                            int n = 0;
                            if (r.charges != null) r.charges.TryGetValue(SoulkeeperState.Kind, out n);
                            SoulkeeperState.UpdateFromState(steam64, n);
                        });
            }
            yield return new WaitForSeconds(IntervalSeconds);
        }
    }

    // Only the field we need; Newtonsoft ignores the rest of the state payload.
    private class ChargesResp { public Dictionary<string, int> charges; }
}

// Local player's death → arm the ward (consume a charge) if one is available.
[HarmonyPatch(typeof(Player), "OnDeath")]
internal static class SoulkeeperOnDeathPatch
{
    private static void Prefix(Player __instance)
    {
        if (__instance == null || __instance != Player.m_localPlayer) return;
        if (SoulkeeperState.TryWardDeath())
        {
            __instance.Message(MessageHud.MessageType.Center,
                               "Soulkeeper Charm — your skills are preserved");
            // The one Soulkeeper charge also arms the Valkyrie carry: the death
            // position is where the tombstone drops, so we bring the player back
            // here on respawn (Phase 2). Captured now, consumed on OnSpawned.
            ValkyrieCarry.ArmCarry(__instance.transform.position);
        }
    }

    // Safety: never leave the ward flag armed past this death. If skills were
    // drained via LowerAllSkills during OnDeath it's already cleared; this clears
    // it for any build that drains skills through a different path.
    private static void Postfix() => SoulkeeperState.ConsumeWardFlag();
}

// Skip the death skill-drain when the death was warded. On a client only the
// local player's skills drain, so no instance check is needed.
[HarmonyPatch(typeof(Skills), "LowerAllSkills")]
internal static class SoulkeeperSkillLossPatch
{
    private static bool Prefix() => !SoulkeeperState.ConsumeWardFlag();
}
