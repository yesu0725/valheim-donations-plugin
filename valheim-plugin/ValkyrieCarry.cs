using System;
using System.Collections;
using HarmonyLib;
using UnityEngine;

// Soulkeeper Charm — Phase 2 (PROTOTYPE): the Valkyrie carry.
//
// A warded death (a Soulkeeper charge was spent — see Soulkeeper.cs) also arms
// a "carry": after respawning at the spawn point the player is told a charge
// was consumed and that the Valkyrie will pick them up in PickupDelaySeconds.
// Then: fade to black → the intro Valkyrie grabs them AT THE SPAWN POINT → fade
// back in mid-flight → it flies the actual spawn→tombstone route and sets them
// down at their grave. No corpse run.
//
// Design notes (v2, after the first live test):
//   * The flight is REAL — no pre-teleport. The vanilla Valkyrie syncs the
//     player + network reference position every FixedUpdate, so zones stream
//     under the flight path exactly like the game's opening cinematic.
//   * Player.AutoPickup NRE'd mid/post-flight in test (items unloading under a
//     fast fly-through). Auto-pickup is disabled for the flight and restored a
//     few seconds after landing.
//   * ESC menu is suppressed during the flight (Menu.Show prefix) and closed if
//     it was open when the pickup happens.
//   * Vanilla Valkyrie.DropPlayer() calls SetIntro(false) on landing; a
//     watchdog polls InIntro() with a distance-scaled hard cap and, if the
//     flight ever stalls, force-clears intro and falls back to a plain distant
//     teleport — it degrades, never soft-locks.
//   * valkyrie_carry_visual=false in valcoin_config.json skips the cutscene and
//     just distant-teleports after the countdown.
public static class ValkyrieCarry
{
    private const float PickupDelaySeconds = 20f;
    private const float FadeSeconds        = 1.2f;

    private static bool    _armed;
    private static Vector3 _targetPos;

    // Menu.Show prefix checks this — true only while the fade/flight runs.
    public static bool FlightActive { get; private set; }

    // --- reflected members (bound once) ---------------------------------
    // Public on their types: TeleportTo, SetIntro, InIntro, IsTeleporting,
    // Valkyrie.m_instance/m_speed/m_turnRate/m_dropHeight/m_startAltitude.
    private static bool _reflectResolved;
    private static System.Reflection.MethodInfo _spawnValkyrie;   // Player, private
    private static System.Reflection.MethodInfo _syncPlayer;      // Valkyrie, private
    private static System.Reflection.FieldInfo  _fTargetPoint;    // Valkyrie privates
    private static System.Reflection.FieldInfo  _fDescentStart;
    private static System.Reflection.FieldInfo  _fFlyAwayPoint;
    private static System.Reflection.FieldInfo  _fDescent;
    private static System.Reflection.FieldInfo  _fDroppedPlayer;
    private static System.Reflection.FieldInfo  _fAutoPickup;     // Player, private static

    // Called from the warded-death hook with the death position (= tombstone).
    public static void ArmCarry(Vector3 deathPos)
    {
        _armed = true;
        _targetPos = deathPos;
        Debug.Log($"[Valcoin][Carry] Armed — will carry to {deathPos} on respawn.");
    }

    // Consumed by the OnSpawned postfix. Disarms so a later natural respawn
    // (e.g. logging back in) never re-triggers it.
    internal static bool ConsumeArmed(out Vector3 pos)
    {
        pos = _targetPos;
        if (!_armed) return false;
        _armed = false;
        return true;
    }

    private static void ResolveReflection()
    {
        if (_reflectResolved) return;
        _reflectResolved = true;
        _spawnValkyrie  = AccessTools.Method(typeof(Player),   "SpawnValkyrie");
        _syncPlayer     = AccessTools.Method(typeof(Valkyrie), "SyncPlayer");
        _fTargetPoint   = AccessTools.Field(typeof(Valkyrie),  "m_targetPoint");
        _fDescentStart  = AccessTools.Field(typeof(Valkyrie),  "m_descentStart");
        _fFlyAwayPoint  = AccessTools.Field(typeof(Valkyrie),  "m_flyAwayPoint");
        _fDescent       = AccessTools.Field(typeof(Valkyrie),  "m_descent");
        _fDroppedPlayer = AccessTools.Field(typeof(Valkyrie),  "m_droppedPlayer");
        _fAutoPickup    = AccessTools.Field(typeof(Player),    "m_enableAutoPickup");
        bool ok = _spawnValkyrie != null && _fTargetPoint != null && _fDescent != null
                  && _fFlyAwayPoint != null && _fDroppedPlayer != null;
        Debug.Log($"[Valcoin][Carry] Reflection bind ok={ok} (spawn={_spawnValkyrie != null}, " +
                  $"sync={_syncPlayer != null}, autoPickup={_fAutoPickup != null})");
    }

    private static bool VisualAvailable =>
        Config.ValkyrieCarryVisual && _spawnValkyrie != null && _fTargetPoint != null
        && _fDescent != null && _fFlyAwayPoint != null && _fDroppedPlayer != null;

    // The carry, from the moment the player respawns at the spawn point.
    internal static IEnumerator DoCarry(Player player, Vector3 gravePos)
    {
        ResolveReflection();

        // Let the respawn finish placing/initialising the player first.
        for (int i = 0; i < 5 && (player == null || player != Player.m_localPlayer); i++)
            yield return null;
        if (Lost(player)) yield break;
        yield return new WaitForSeconds(0.5f);
        if (Lost(player)) yield break;

        // 1) Announce: charge consumed + pickup countdown.
        player.Message(MessageHud.MessageType.Center,
            $"Soulkeeper Charm — 1 charge consumed.\nA Valkyrie will carry you to your tombstone in {PickupDelaySeconds:0} seconds.");

        yield return new WaitForSeconds(PickupDelaySeconds - 5f);
        if (Lost(player)) yield break;
        player.Message(MessageHud.MessageType.Center, "The Valkyrie descends...");
        yield return new WaitForSeconds(5f);
        if (Lost(player)) yield break;

        if (VisualAvailable)
            yield return CarryFlight(player, gravePos);
        else
        {
            FallbackTeleport(player, gravePos);
            yield return RepelPulses(gravePos);
        }
    }

    // Player gone or died again during the countdown (a new death re-arms with
    // the newer grave position, so just drop this carry).
    private static bool Lost(Player player)
        => player == null || player != Player.m_localPlayer || player.IsDead();

    // ─────────────────────────────────────────────────────────────────────
    // The flight: spawn point → tombstone
    // ─────────────────────────────────────────────────────────────────────
    private static IEnumerator CarryFlight(Player player, Vector3 gravePos)
    {
        // 2) Fade to black over the pickup moment.
        FlightActive = true;                       // ESC menu suppressed from here
        try { if (Menu.IsVisible()) Menu.instance?.Hide(); } catch { }
        yield return CarryFadeOverlay.FadeTo(1f, FadeSeconds);
        if (Lost(player)) { FlightActive = false; yield return CarryFadeOverlay.FadeTo(0f, 0.3f); yield break; }

        bool prevAutoPickup = SetAutoPickup(false); // items unloading under the flight NRE'd AutoPickup in test

        // 3) Spawn the vanilla Valkyrie, then re-route it: its Awake targeted the
        //    player's CURRENT position (the spawn point) from 500m out — we
        //    instead start it right here and point it at the tombstone, so the
        //    flight is the real spawn→grave route.
        Valkyrie valk = null;
        try
        {
            player.SetIntro(true);
            _spawnValkyrie.Invoke(player, null);
            valk = Valkyrie.m_instance;
            if (valk != null)
            {
                Vector3 start = player.transform.position + Vector3.up * valk.m_dropHeight;
                Vector3 target = gravePos + Vector3.up * valk.m_dropHeight;
                Vector3 dir = target - start; dir.y = 0f;
                float dist = dir.magnitude;
                dir = dist > 0.01f ? dir / dist : Vector3.forward;

                valk.transform.position = start;
                valk.transform.rotation = Quaternion.LookRotation(dir);
                _fTargetPoint.SetValue(valk, target);
                _fDescentStart.SetValue(valk, start);      // unused once m_descent=true
                _fDescent.SetValue(valk, true);            // skip the 500m/500alt approach
                _fDroppedPlayer.SetValue(valk, false);
                Vector3 flyAway = target + dir * 200f; flyAway.y = valk.m_startAltitude;
                _fFlyAwayPoint.SetValue(valk, flyAway);

                // Scale speed to the route so long corpse runs don't take all day:
                // aim ~35s of flight, clamped to keep zone streaming comfortable.
                valk.m_speed = Mathf.Clamp(dist / 35f, 12f, 60f);
                valk.m_turnRate = 20f;                     // line up on the route quickly

                // Its Awake yanked the player to the vanilla far-out start for one
                // frame — snap them back onto the attach point at OUR start now.
                try { _syncPlayer?.Invoke(valk, new object[] { true }); } catch { }

                Debug.Log($"[Valcoin][Carry] Flight configured: dist={dist:0}m speed={valk.m_speed:0.0} → {target}");
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Valcoin][Carry] Valkyrie setup threw: {ex.Message}");
        }

        if (valk == null)
        {
            // Couldn't stage the cutscene — restore and take the reliable path.
            try { player.SetIntro(false); } catch { }
            SetAutoPickup(prevAutoPickup);
            FlightActive = false;
            yield return CarryFadeOverlay.FadeTo(0f, 0.3f);
            FallbackTeleport(player, gravePos);
            yield break;
        }

        // 4) Fade back in on the flight already underway.
        yield return CarryFadeOverlay.FadeTo(0f, FadeSeconds);

        // Watchdog: vanilla DropPlayer() clears InIntro on landing. Cap scales
        // with the route so a genuinely long flight isn't cut short, but a stall
        // can't hold the player forever.
        float est = Vector3.Distance(player.transform.position, gravePos) / Mathf.Max(valk.m_speed, 1f);
        float hardCap = est * 2f + 45f;
        float elapsed = 0f;
        bool landed = false;
        while (elapsed < hardCap)
        {
            if (player == null || player != Player.m_localPlayer) break;
            if (!player.InIntro()) { landed = true; break; }
            elapsed += 0.5f;
            yield return new WaitForSeconds(0.5f);
        }

        FlightActive = false;
        if (landed)
        {
            Debug.Log($"[Valcoin][Carry] Valkyrie delivered player (t={elapsed:0.0}s).");
        }
        else if (player != null && player == Player.m_localPlayer)
        {
            Debug.LogWarning("[Valcoin][Carry] Flight watchdog hit hard cap — forcing landing.");
            try { player.SetIntro(false); } catch { }
            FallbackTeleport(player, gravePos);
        }

        // Drive hostile creatures away from the tombstone so the player isn't
        // mobbed the moment control returns. The pulses double as the settle
        // delay before auto-pickup is restored (the post-flight AutoPickup NRE
        // from the first test).
        yield return RepelPulses(gravePos);
        SetAutoPickup(prevAutoPickup);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Tomb-side repel: scatter creatures camping the tombstone
    // ─────────────────────────────────────────────────────────────────────
    // Three shockwave pulses over ~4.5s. Each pulse hits every hostile
    // creature near the grave with a ZERO-damage, NO-attacker HitData that
    // carries only stagger + a strong radial push. Verified against
    // RPC_Damage: no attacker + no damage means no aggro/aggravation — the
    // owner just applies the pushback — and Damage() routes to the creature's
    // owner, so it works no matter which peer simulates the creature.
    private const float RepelRadius = 12f;
    private const float RepelForce  = 90f;

    private static IEnumerator RepelPulses(Vector3 gravePos)
    {
        for (int pulse = 0; pulse < 3; pulse++)
        {
            RepelPulse(gravePos, RepelRadius, RepelForce);
            yield return new WaitForSeconds(1.5f);
        }
    }

    private static void RepelPulse(Vector3 center, float radius, float force)
    {
        try
        {
            int pushed = 0;
            foreach (var c in Character.GetAllCharacters())
            {
                if (c == null || c.IsPlayer() || c.IsTamed() || c.IsBoss() || c.IsDead()) continue;
                if (Vector3.Distance(c.transform.position, center) > radius) continue;

                var dir = c.transform.position - center;
                dir.y = 0f;
                if (dir.sqrMagnitude < 0.01f) dir = UnityEngine.Random.insideUnitSphere;

                var hit = new HitData();
                hit.m_dir = dir.normalized;
                hit.m_pushForce = force;
                hit.m_staggerMultiplier = 100f;   // >= 100 staggers on the owner
                hit.m_point = c.transform.position;
                c.Damage(hit);
                pushed++;
            }
            if (pushed > 0)
                Debug.Log($"[Valcoin][Carry] Repel pulse pushed {pushed} creature(s) from the tomb.");
        }
        catch (Exception ex) { Debug.LogWarning($"[Valcoin][Carry] Repel failed: {ex.Message}"); }
    }

    // Sets Player.m_enableAutoPickup (private static, user-toggleable in vanilla
    // — so save and restore whatever the player had). Returns the prior value.
    private static bool SetAutoPickup(bool enabled)
    {
        try
        {
            if (_fAutoPickup == null) return true;
            bool prev = (bool)_fAutoPickup.GetValue(null);
            _fAutoPickup.SetValue(null, enabled);
            return prev;
        }
        catch { return true; }
    }

    // The reliable no-cutscene path: portal-grade distant teleport (its own
    // loading fade covers the transition).
    private static void FallbackTeleport(Player player, Vector3 gravePos)
    {
        Vector3 dst = gravePos + Vector3.up * 0.5f;
        bool teleported = false;
        try { teleported = player.TeleportTo(dst, player.transform.rotation, true); }
        catch (Exception ex) { Debug.LogError($"[Valcoin][Carry] TeleportTo threw: {ex.Message}"); }
        if (!teleported)
        {
            try { player.transform.position = dst; teleported = true; }
            catch (Exception ex) { Debug.LogError($"[Valcoin][Carry] Hard reposition failed: {ex.Message}"); }
        }
        Debug.Log($"[Valcoin][Carry] Fallback teleport issued={teleported} → {dst}");
    }
}

// Full-screen black fade drawn over everything (IMGUI, same surface the panel
// uses — no dependency on Hud internals). Driven only by ValkyrieCarry.
public class CarryFadeOverlay : MonoBehaviour
{
    private static CarryFadeOverlay _instance;
    private float _alpha;

    private static CarryFadeOverlay Ensure()
    {
        if (_instance == null)
        {
            var go = new GameObject("ValcoinCarryFade");
            _instance = go.AddComponent<CarryFadeOverlay>();
            DontDestroyOnLoad(go);
        }
        return _instance;
    }

    public static IEnumerator FadeTo(float target, float seconds)
    {
        var o = Ensure();
        float from = o._alpha;
        float t = 0f;
        while (t < seconds)
        {
            t += Time.deltaTime;
            o._alpha = Mathf.Lerp(from, target, Mathf.Clamp01(t / seconds));
            yield return null;
        }
        o._alpha = target;
    }

    private void OnGUI()
    {
        if (_alpha <= 0.001f) return;
        GUI.depth = -10000;                     // in front of everything IMGUI
        var prev = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, _alpha);
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
        GUI.color = prev;
    }
}

// No ESC menu during the flight (the player has no control to give it anyway).
[HarmonyPatch(typeof(Menu), "Show")]
internal static class ValkyrieCarryMenuPatch
{
    private static bool Prefix() => !ValkyrieCarry.FlightActive;
}

// On respawn, the game re-instantiates the player and calls OnSpawned(bool). If
// a warded death armed a carry, run it now. (First-spawn intro isn't affected:
// the carry is only armed by a death this session, which never coincides with a
// brand-new character's very first spawn.)
[HarmonyPatch(typeof(Player), "OnSpawned", new[] { typeof(bool) })]
internal static class ValkyrieCarryOnSpawnedPatch
{
    private static void Postfix(Player __instance)
    {
        try
        {
            if (__instance == null || __instance != Player.m_localPlayer) return;
            if (!ValkyrieCarry.ConsumeArmed(out var pos)) return;
            if (SharedCoroutineRunner.Instance == null) return;
            SharedCoroutineRunner.Instance.StartCoroutine(ValkyrieCarry.DoCarry(__instance, pos));
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Valcoin][Carry] OnSpawned postfix error: {ex.Message}");
        }
    }
}
