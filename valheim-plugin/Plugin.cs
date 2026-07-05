using BepInEx;
using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using HarmonyLib;
// Alias the plugin's static Config class so it doesn't collide with the
// inherited BaseUnityPlugin.Config (a BepInEx ConfigFile) property.
using PluginConfig = Config;

[BepInPlugin("com.taeguk.valheimdonations", "Valheim Donations", "5.0.0")]
public class Plugin : BaseUnityPlugin
{
    public static HashSet<string> AdminSteamIDs = new HashSet<string>();

    private Harmony _harmony;
    private static readonly string AdminConfigPath = Path.Combine(Paths.ConfigPath, "valcoin_admins.yaml");

    private void Awake()
    {
        Logger.LogInfo("[Valheim Donations] Plugin loaded");

        PluginConfig.Load();
        EnsureAdminFile();
        LoadAdmins();
        CoinManager.Load();
        PerkManager.Load();
        Catalog.Load();

        // GrantPoller drives itself; only useful on dedicated/host servers.
        var go = new GameObject("ValcoinGrantPoller");
        go.AddComponent<GrantPoller>();
        DontDestroyOnLoad(go);

        _harmony = new Harmony("com.taeguk.valheimdonations");
        _harmony.PatchAll();

        // RPC layer + client-side UI panel. Both halves are spawned regardless
        // of role; each branch self-detects via ZNet.IsServer() at runtime.
        StartCoroutine(InitRpcsWhenReady());
        SpawnClientUiIfNotServer();

        Logger.LogInfo($"Startup complete. Admins: {AdminSteamIDs.Count}, Backend ready: {PluginConfig.Ready}");
    }

    private void OnDestroy() => _harmony?.UnpatchSelf();

    private System.Collections.IEnumerator InitRpcsWhenReady()
    {
        // Wait for ZNet so we know whether we're a server or a client first.
        while (ZNet.instance == null) yield return null;
        bool serverSide = ZNet.instance.IsServer();
        yield return RpcLayer.RegisterWhenReady(serverSide);
    }

    private void SpawnClientUiIfNotServer()
    {
        // The DonationPanel checks IsServer in Update; spawning unconditionally
        // is safe (OnGUI is a no-op on a headless server with no display). But
        // skipping the GameObject on a dedicated server avoids the per-frame
        // Update tick entirely.
        StartCoroutine(SpawnUiWhenZnetReady());
    }

    private System.Collections.IEnumerator SpawnUiWhenZnetReady()
    {
        while (ZNet.instance == null) yield return null;
        if (ZNet.instance.IsServer() && ZNet.instance.IsDedicated()) yield break;

        var go = new GameObject("ValcoinDonationPanel");
        go.AddComponent<DonationPanel>();
        DontDestroyOnLoad(go);

        // F4 Donation Codex — the browsable, offline-resilient home for the
        // donation system. Sits alongside the F8 quick panel.
        var codex = new GameObject("ValcoinDonationCodex");
        codex.AddComponent<DonationCodex>();
        DontDestroyOnLoad(codex);
    }

    // --- Admin YAML --------------------------------------------------------

    private static void EnsureAdminFile()
    {
        try
        {
            Directory.CreateDirectory(Paths.ConfigPath);
            if (File.Exists(AdminConfigPath)) return;

            File.WriteAllText(AdminConfigPath,
@"# Valcoin Admins (Steam64 IDs)
# ------------------------------------------------------------
# Add Steam64 IDs here to grant admin permission for:
#   /givecoins <playerName> <amount>
#   /removecoins <playerName> <amount>
#
# Find your Steam64 at https://steamid.io
# Restart the server after changes.
admins:
  - 76561198012345678   # <-- replace
");
            Debug.LogWarning($"[Valcoin] Created admin file template at: {AdminConfigPath}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Valcoin] Failed to create admin YAML: {ex.Message}");
        }
    }

    private static void LoadAdmins()
    {
        try
        {
            var ids = new HashSet<string>();
            if (!File.Exists(AdminConfigPath)) { AdminSteamIDs = ids; return; }

            bool inAdmins = false;
            var listItem = new Regex(@"^\s*-\s*(\d{17})\b", RegexOptions.Compiled);

            foreach (var raw in File.ReadAllLines(AdminConfigPath))
            {
                var line = raw.TrimEnd();
                if (line.TrimStart().StartsWith("#")) continue;

                if (!inAdmins)
                {
                    if (Regex.IsMatch(line, @"^\s*admins\s*:\s*$")) inAdmins = true;
                    continue;
                }

                var m = listItem.Match(line);
                if (m.Success) { ids.Add(m.Groups[1].Value); continue; }

                if (Regex.IsMatch(line, @"^\s*\w+\s*:\s*$")) break; // next top-level key
            }

            AdminSteamIDs = ids;
            Debug.Log($"[Valcoin] Loaded {ids.Count} admin Steam64 ID(s).");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Valcoin] Failed to load admin YAML: {ex.Message}");
            AdminSteamIDs = new HashSet<string>();
        }
    }
}
