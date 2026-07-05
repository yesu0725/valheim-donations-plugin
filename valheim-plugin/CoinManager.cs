using BepInEx;
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

public static class CoinManager
{
    private static readonly string SaveDir  = Path.Combine(Paths.ConfigPath, "valcoin_data");
    private static readonly string SaveFile = Path.Combine(SaveDir, "coin_balances.json");

    private const int RecentGrantCap = 5000;

    private class State
    {
        public Dictionary<string, int> balances    = new Dictionary<string, int>();
        public List<long>              recentGrants = new List<long>();   // FIFO of applied grant ids
    }

    private static State _state = new State();
    private static HashSet<long> _seen = new HashSet<long>();

    public static void Load()
    {
        try
        {
            Directory.CreateDirectory(SaveDir);
            if (File.Exists(SaveFile))
            {
                var raw = File.ReadAllText(SaveFile);
                // Tolerate the legacy shape (a bare {steamId: int} map).
                State loaded;
                try
                {
                    loaded = JsonConvert.DeserializeObject<State>(raw);
                    if (loaded == null || loaded.balances == null) throw new Exception("not new shape");
                }
                catch
                {
                    var legacy = JsonConvert.DeserializeObject<Dictionary<string, int>>(raw)
                                 ?? new Dictionary<string, int>();
                    loaded = new State { balances = legacy };
                }
                _state = loaded;
                _state.recentGrants ??= new List<long>();
                _seen = new HashSet<long>(_state.recentGrants);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[CoinManager] Failed to load: {ex.Message}");
            _state = new State();
            _seen = new HashSet<long>();
        }
    }

    public static void Save()
    {
        try
        {
            File.WriteAllText(SaveFile, JsonConvert.SerializeObject(_state, Formatting.Indented));
        }
        catch (Exception ex)
        {
            Debug.LogError($"[CoinManager] Failed to save: {ex.Message}");
        }
    }

    public static int GetBalance(string steamId)
    {
        return _state.balances.TryGetValue(steamId, out var v) ? v : 0;
    }

    public static void AddCoins(string steamId, int amount)
    {
        _state.balances[steamId] = GetBalance(steamId) + amount;
        Save();
    }

    public static void SetBalance(string steamId, int amount)
    {
        _state.balances[steamId] = Math.Max(0, amount);
        Save();
    }

    /// Apply a grant atomically with a local seen-ids cache.
    /// Returns true if applied, false if it was a replay (caller should still ack).
    public static bool TryApplyGrant(long grantId, string steamId, int amount)
    {
        if (_seen.Contains(grantId)) return false;
        _seen.Add(grantId);
        _state.recentGrants.Add(grantId);
        if (_state.recentGrants.Count > RecentGrantCap)
        {
            int drop = _state.recentGrants.Count - RecentGrantCap;
            var dropped = _state.recentGrants.GetRange(0, drop);
            _state.recentGrants.RemoveRange(0, drop);
            foreach (var id in dropped) _seen.Remove(id);
        }
        AddCoins(steamId, amount);   // also Saves
        return true;
    }
}
