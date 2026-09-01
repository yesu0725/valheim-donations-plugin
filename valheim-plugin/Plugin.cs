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

[BepInPlugin("com.taeguk.valheimdonations", "Valheim Donations", "5.23.0")]
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
        QuestCatalog.Load();
        // NOTE: the familiar layout is deliberately NOT loaded here. It is a
        // client-only cosmetic file, so ArmorVfxManager loads it -- and that
        // component is never spawned on a dedicated server, which is how the
        // server avoids growing a config file it would never read.

        // GrantPoller drives itself; only useful on dedicated/host servers.
        var go = new GameObject("ValcoinGrantPoller");
        go.AddComponent<GrantPoller>();
        DontDestroyOnLoad(go);

        // CatalogSync likewise self-detects server role and is a no-op on
        // remote clients (see CatalogSync.Loop).
        var catalogSyncGo = new GameObject("ValcoinCatalogSync");
        catalogSyncGo.AddComponent<CatalogSync>();
        DontDestroyOnLoad(catalogSyncGo);

        _harmony = new Harmony("com.taeguk.valheimdonations");
        _harmony.PatchAll();

        // Client-side UI panel. Spawned regardless of role; it self-detects via
        // ZNet.IsServer() at runtime. (The RPC handlers are registered from
        // Update instead -- see the note there.)
        SpawnClientUiIfNotServer();

        Logger.LogInfo($"Startup complete. Admins: {AdminSteamIDs.Count}, Backend ready: {PluginConfig.Ready}");
    }

    private void OnDestroy() => _harmony?.UnpatchSelf();

    // Keeps the RPC handlers registered for the CURRENT session, every session.
    //
    // This used to be a one-shot coroutine that waited for ZNet and registered
    // once. That is wrong for a client: ZNet — and with it ZRoutedRpc, which owns
    // the handler table — is destroyed when you log out to the main menu and
    // rebuilt when you log back in, while the plugin stays loaded for the life of
    // the process. So from the second session onward the handlers were gone and
    // nothing put them back, which is why donations and purchases worked exactly
    // until the first relog and then took coins without delivering (the full
    // story is on RpcLayer's registration fields). Restarting the game fixed it
    // only because that reloaded the plugin.
    //
    // Running it per-frame instead of on a timer means the handlers are back
    // before the connection is up, so nothing can arrive in a gap. The work is
    // two reference comparisons once registration has happened.
    private void Update()
    {
        var znet = ZNet.instance;
        if (znet == null || ZRoutedRpc.instance == null)
        {
            // Between sessions. Forgetting here is what arms the re-registration
            // and lets the dead ZRoutedRpc be collected.
            RpcLayer.Forget();
            return;
        }

        // A listen server (host) is BOTH: it answers client actions and it has a
        // local player who needs the client-side replies. Registering only one
        // side left a host unable to receive panel messages, the catalog, or the
        // quest ack, which would have left its own player re-reporting a completed
        // quest forever. Only a dedicated server genuinely has no client half.
        // The per-instance guards inside make the double call safe, and the two
        // RPC name sets don't overlap.
        //
        // Both roles are re-read every frame rather than latched, so a player who
        // leaves a dedicated server and then hosts their own world (or the
        // reverse) gets the right half registered for the session they're in.
        if (znet.IsServer()) RpcLayer.Register(serverSide: true);
        if (!znet.IsDedicated()) RpcLayer.Register(serverSide: false);
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

        // Single combined donation panel — opens with either F8 or F4 (both
        // keys point at this one panel now; see DonationPanel).
        var go = new GameObject("ValcoinDonationPanel");
        go.AddComponent<DonationPanel>();
        DontDestroyOnLoad(go);

        // Keeps the local player's Soulkeeper charge count fresh so a death is
        // warded even if the shop panel was never opened this session.
        var sk = new GameObject("ValcoinSoulkeeperPoller");
        sk.AddComponent<SoulkeeperPoller>();
        DontDestroyOnLoad(sk);

        // Renders armor-effect auras (self + other players) from ZDO state and
        // mirrors the local player's equipped auras onto their own ZDO.
        var av = new GameObject("ValcoinArmorVfxManager");
        av.AddComponent<ArmorVfxManager>();
        DontDestroyOnLoad(av);

        // Watches for the "VC.Q.*" player keys ServerGuide quests set on
        // completion. Client-side only: quest state lives on the character, so
        // the dedicated server never sees these keys itself.
        var qw = new GameObject("ValcoinQuestWatcher");
        qw.AddComponent<QuestWatcher>();
        DontDestroyOnLoad(qw);
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
# Add Steam64 IDs here to grant admin permission for the Admin tab in the
# F8 quick panel (give/remove a player's Valcoin balance).
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
