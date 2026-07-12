using System;
using UnityEngine;

// Resolves THIS client's own Steam64. SteamIdResolver only handles remote peers
// (from their sockets); the local player has no peer entry, so we go straight to
// Steamworks. Shared by the donation panel and the Soulkeeper charge poller so
// each can resolve the local id without the panel having to be open first.
public static class LocalIdentity
{
    private static string _cached;

    public static string Steam64()
    {
        if (!string.IsNullOrEmpty(_cached)) return _cached;

        const System.Reflection.BindingFlags PubStatic =
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;

        // 1) Steamworks.NET — the canonical source of the local Steam64. Scan
        //    loaded assemblies rather than guess the assembly name.
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t;
                try { t = asm.GetType("Steamworks.SteamUser"); } catch { continue; }
                if (t == null) continue;

                var cid = t.GetMethod("GetSteamID", PubStatic)?.Invoke(null, null);
                var raw = cid?.GetType().GetField("m_SteamID")?.GetValue(cid)?.ToString();
                if (!string.IsNullOrEmpty(raw) && raw.Length == 17 && raw.StartsWith("7656119"))
                {
                    _cached = raw;
                    Debug.Log($"[Valcoin] Local Steam64 resolved via Steamworks: {raw}");
                    return raw;
                }
            }
        }
        catch (Exception ex) { Debug.LogWarning("[Valcoin] Steamworks id lookup failed: " + ex.Message); }

        // 2) Fallback: the older ZSteamMatchmaking.GetSteamID path.
        try
        {
            var t = Type.GetType("ZSteamMatchmaking, assembly_valheim");
            var inst = t?.GetField("instance", PubStatic)?.GetValue(null);
            var id = inst?.GetType().GetMethod("GetSteamID")?.Invoke(inst, null)?.ToString();
            if (!string.IsNullOrEmpty(id) && id.Length == 17 && id.StartsWith("7656119"))
            {
                _cached = id;
                Debug.Log($"[Valcoin] Local Steam64 resolved via ZSteamMatchmaking: {id}");
                return id;
            }
        }
        catch { }

        return null;
    }
}
