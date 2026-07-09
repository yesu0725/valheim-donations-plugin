using BepInEx;
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

// Per-player perks state. Persisted at BepInEx/config/valcoin_data/perks.json.
//
// Stores:
//   - unlocked perks (passive flags like donor_badge, sethome)
//   - charges of consumable perks (e.g. /shout)
//   - chat title text (set via the Gift tab's title editor once chat_title is unlocked)
//   - saved home position + last home-teleport time (for /home cooldown)
//
public static class PerkManager
{
    private static readonly string SaveDir  = Path.Combine(Paths.ConfigPath, "valcoin_data");
    private static readonly string SaveFile = Path.Combine(SaveDir, "perks.json");

    public class Pos { public float x, y, z; }

    public class PlayerPerks
    {
        public HashSet<string>      perks   = new HashSet<string>();   // donor_badge, chat_title, sethome
        public Dictionary<string,int> charges = new Dictionary<string,int>(); // shout: 3
        public string title;
        public Pos    home;
        public string homeCooldownUntilUtc;   // ISO 8601
    }

    private class State { public Dictionary<string, PlayerPerks> players = new Dictionary<string, PlayerPerks>(); }

    private static State _state = new State();

    public static void Load()
    {
        try
        {
            Directory.CreateDirectory(SaveDir);
            if (File.Exists(SaveFile))
            {
                _state = JsonConvert.DeserializeObject<State>(File.ReadAllText(SaveFile)) ?? new State();
                if (_state.players == null) _state.players = new Dictionary<string, PlayerPerks>();
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[PerkManager] Failed to load: {ex.Message}");
            _state = new State();
        }
    }

    public static void Save()
    {
        try { File.WriteAllText(SaveFile, JsonConvert.SerializeObject(_state, Formatting.Indented)); }
        catch (Exception ex) { Debug.LogError($"[PerkManager] Failed to save: {ex.Message}"); }
    }

    public static PlayerPerks Get(string steam64)
    {
        if (string.IsNullOrEmpty(steam64)) return new PlayerPerks();
        if (!_state.players.TryGetValue(steam64, out var p))
        {
            p = new PlayerPerks();
            _state.players[steam64] = p;
        }
        return p;
    }

    public static bool Has(string steam64, string perk)
    {
        if (string.IsNullOrEmpty(steam64)) return false;
        return _state.players.TryGetValue(steam64, out var p) && p.perks.Contains(perk);
    }

    public static void Grant(string steam64, string perk)
    {
        Get(steam64).perks.Add(perk);
        Save();
    }

    public static void AddCharges(string steam64, string perk, int n)
    {
        var p = Get(steam64);
        p.charges.TryGetValue(perk, out var cur);
        p.charges[perk] = cur + n;
        Save();
    }

    public static int Charges(string steam64, string perk)
    {
        if (string.IsNullOrEmpty(steam64)) return 0;
        return _state.players.TryGetValue(steam64, out var p) && p.charges.TryGetValue(perk, out var c) ? c : 0;
    }

    /// Returns true if a charge was successfully consumed.
    public static bool ConsumeCharge(string steam64, string perk)
    {
        var p = Get(steam64);
        if (!p.charges.TryGetValue(perk, out var c) || c <= 0) return false;
        p.charges[perk] = c - 1;
        Save();
        return true;
    }

    public static void SetTitle(string steam64, string title)
    {
        Get(steam64).title = title;
        Save();
    }

    public static string Title(string steam64) =>
        _state.players.TryGetValue(steam64, out var p) ? p.title : null;

    public static void SetHome(string steam64, Vector3 pos)
    {
        Get(steam64).home = new Pos { x = pos.x, y = pos.y, z = pos.z };
        Save();
    }

    public static Vector3? Home(string steam64)
    {
        if (!_state.players.TryGetValue(steam64, out var p) || p.home == null) return null;
        return new Vector3(p.home.x, p.home.y, p.home.z);
    }

    /// Returns 0 if ready, or seconds remaining until ready.
    public static int HomeCooldownRemaining(string steam64)
    {
        var p = Get(steam64);
        if (string.IsNullOrEmpty(p.homeCooldownUntilUtc)) return 0;
        if (!DateTime.TryParse(p.homeCooldownUntilUtc, null,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var until))
            return 0;
        var remaining = (int)(until - DateTime.UtcNow).TotalSeconds;
        return Math.Max(0, remaining);
    }

    public static void StartHomeCooldown(string steam64, int seconds)
    {
        Get(steam64).homeCooldownUntilUtc =
            DateTime.UtcNow.AddSeconds(seconds).ToString("o");
        Save();
    }
}
