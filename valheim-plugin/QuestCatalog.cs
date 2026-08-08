using BepInEx;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using UnityEngine;

// Loads BepInEx/config/valcoin_quests.yaml — the map from a ServerGuide quest to
// its Valcoin payout.
//
//   quests:
//     vc_welcome:
//       name: "The Patron's Welcome"
//       coins: 30
//       period: once          # once | daily
//
// ServerGuide grants no currency (its reward types are items/skills/buffs/keys),
// so a quest signals completion by setting a player key `VC.Q.<id>` via its
// stock `set_player_key` reward. QuestWatcher (client) spots the key and reports
// it; QuestFlow (server) looks the id up *here* and tells the backend what it's
// worth. The coin value never travels from the client — a fabricated report can
// only name a quest, not price it.
//
// Same hand-rolled parser as Catalog.cs, for the same reason: no YamlDotNet
// dependency, and the format is deliberately flat.
public static class QuestCatalog
{
    public class Quest
    {
        public string Id;        // map key; the player key is "VC.Q." + Id
        public string Name;      // human label used in panel/toast messages
        public int    Coins;
        public string Period = "daily";   // once | daily
    }

    public static Dictionary<string, Quest> Items { get; private set; } = new Dictionary<string, Quest>();
    public static List<Quest>               Order { get; private set; } = new List<Quest>();

    /// Prefix of the ServerGuide player key a quest sets to signal completion.
    public const string KeyPrefix = "VC.Q.";

    private static readonly string QuestPath = Path.Combine(Paths.ConfigPath, "valcoin_quests.yaml");

    private static readonly Regex QuestRe = new Regex(@"^\s{2}([A-Za-z0-9_]+)\s*:\s*$", RegexOptions.Compiled);
    private static readonly Regex FieldRe = new Regex(@"^\s{4}([a-z_]+)\s*:\s*(.+?)\s*$", RegexOptions.Compiled);

    public static string KeyFor(string questId) => KeyPrefix + questId;

    public static Quest Get(string questId)
    {
        if (string.IsNullOrEmpty(questId)) return null;
        return Items.TryGetValue(questId, out var q) ? q : null;
    }

    public static void Load()
    {
        EnsureFile();
        try
        {
            Parse(File.ReadAllLines(QuestPath));
            Debug.Log($"[Valcoin] Quest catalog loaded: {Items.Count} quest(s).");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Valcoin] Failed to parse quest catalog: {ex.Message}");
            Items = new Dictionary<string, Quest>();
            Order = new List<Quest>();
        }
    }

    private static void EnsureFile()
    {
        if (File.Exists(QuestPath)) return;
        try
        {
            File.WriteAllText(QuestPath,
@"# Valcoin quest rewards
# -----------------------------------------------------------------------
# Maps a ServerGuide quest to its Valcoin payout. The quest itself lives in
# ServerGuide's own config (guidance.valcoin-quests.yaml); all it does to earn
# coins is set the player key ""VC.Q.<id>"" with a set_player_key reward.
#
#   coins:   payout. Daily quests are clamped by the backend's per-day cap, so
#            the pool below deliberately sums to more than a player can earn.
#   period:  daily = once per UTC day · once = a single time per character
#
# The backend is the only authority on whether a report actually pays — this
# file just prices the quests. Edit and restart the server to apply changes.
quests:

  # ---------- One-time onboarding ----------
  vc_welcome:
    name: ""The Patron's Welcome""
    coins: 30
    period: once

  # ---------- Dailies ----------
  daily_horn:
    name: ""Answer the Horn""
    coins: 2
    period: daily

  daily_hunt:
    name: ""Thin the Wilds""
    coins: 3
    period: daily

  daily_tame:
    name: ""Tend the Beasts""
    coins: 2
    period: daily

  daily_lord:
    name: ""Fell a Lord""
    coins: 8
    period: daily

  daily_bond:
    name: ""Forge a Bond""
    coins: 5
    period: daily
");
            Debug.Log($"[Valcoin] Created quest catalog template at {QuestPath}.");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Valcoin] Failed to create quest catalog: {ex.Message}");
        }
    }

    private static void Parse(string[] lines)
    {
        var items = new Dictionary<string, Quest>();
        var order = new List<Quest>();

        bool inQuests = false;
        Quest current = null;

        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (raw.TrimStart().StartsWith("#")) continue;

            if (!inQuests)
            {
                if (Regex.IsMatch(raw, @"^quests\s*:\s*$")) inQuests = true;
                continue;
            }

            var questMatch = QuestRe.Match(raw);
            if (questMatch.Success)
            {
                Commit(current, items, order);
                current = new Quest { Id = questMatch.Groups[1].Value };
                continue;
            }

            var fieldMatch = FieldRe.Match(raw);
            if (current == null || !fieldMatch.Success) continue;

            var value = StripQuotes(fieldMatch.Groups[2].Value);
            switch (fieldMatch.Groups[1].Value)
            {
                case "name":   current.Name = value; break;
                case "period": current.Period = value.ToLowerInvariant(); break;
                case "coins":
                    if (int.TryParse(value, out var coins)) current.Coins = coins;
                    break;
            }
        }

        Commit(current, items, order);
        Items = items;
        Order = order;
    }

    private static void Commit(Quest q, Dictionary<string, Quest> items, List<Quest> order)
    {
        if (q == null || string.IsNullOrEmpty(q.Id)) return;
        if (q.Coins <= 0)
        {
            Debug.LogWarning($"[Valcoin] Quest '{q.Id}' has no positive coins value; skipping.");
            return;
        }
        if (q.Period != "once" && q.Period != "daily")
        {
            Debug.LogWarning($"[Valcoin] Quest '{q.Id}' has unknown period '{q.Period}'; defaulting to daily.");
            q.Period = "daily";
        }
        if (string.IsNullOrEmpty(q.Name)) q.Name = q.Id;

        items[q.Id] = q;
        order.Add(q);
    }

    // --- Server -> client sync (mirrors Catalog.Serialize / ApplyRemote) -----
    //
    // valcoin_quests.yaml only exists on the machine that loaded it, so a remote
    // client has no idea which player keys to watch for. The server pushes the
    // list; the client only ever uses the ids and names.

    public static string Serialize()
    {
        try { return JsonConvert.SerializeObject(Order); }
        catch (Exception ex)
        {
            Debug.LogError($"[Valcoin] Quest catalog serialize failed: {ex.Message}");
            return null;
        }
    }

    public static void ApplyRemote(string json)
    {
        if (string.IsNullOrEmpty(json)) return;
        try
        {
            var order = JsonConvert.DeserializeObject<List<Quest>>(json);
            if (order == null) return;

            var items = new Dictionary<string, Quest>();
            foreach (var q in order)
                if (!string.IsNullOrEmpty(q.Id)) items[q.Id] = q;

            Items = items;
            Order = order;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Valcoin] Quest catalog ApplyRemote failed: {ex.Message}");
        }
    }

    private static string StripQuotes(string v)
    {
        if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"')
            return v.Substring(1, v.Length - 2);
        return v;
    }
}
