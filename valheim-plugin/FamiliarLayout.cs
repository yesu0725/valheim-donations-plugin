using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using UnityEngine;

// Per-familiar hover position, editable at runtime.
//
// Familiars used to hover at one hard-coded spot for everyone —
// ArmorVfx.CompanionOffset plus the aura's own `Raise` nudge. That is a fine
// default and it is still exactly what this file is seeded with, but it is a
// matter of taste, and taste is not something to recompile for. This reads
// `BepInEx/config/valcoin_familiars.yaml` and re-reads it whenever the file's
// timestamp moves, so a player can drag a number and watch the familiar move
// without leaving the world.
//
// CLIENT-SIDE AND COSMETIC. Familiars are drawn by each client from the
// wearer's ZDO (see ArmorVfxManager), so this file changes what YOU see —
// including where other players' familiars sit on your screen. It is not
// synced, not authoritative, and a dedicated server never reads it. Nothing
// here touches the ledger, prices, or any perk.
//
// The parser is the same hand-rolled, regex-per-line kind used for
// valcoin_admins.yaml and valcoin_shop.yaml, for the same reason: the plugin
// deliberately carries no YamlDotNet dependency (see docs/THUNDERSTORE.md).
public static class FamiliarLayout
{
    private static readonly string ConfigPath =
        Path.Combine(Paths.ConfigPath, "valcoin_familiars.yaml");

    // auraId -> offset from the player root, in the player's own space:
    //   x  negative = the player's LEFT, positive = right
    //   y  height (1.55 is roughly head height)
    //   z  positive = in front of the player, negative = behind
    private static Dictionary<string, Vector3> _offsets = new Dictionary<string, Vector3>();

    // Bumped on every successful reload. ArmorVfxManager watches this and
    // repositions the familiars it already has on screen, which is what makes
    // an edit show up without re-equipping the helmet.
    public static int Version { get; private set; }

    private static DateTime _stamp;
    private static float _nextCheck;

    // How often the file's timestamp is looked at. One stat per second is
    // nothing, and it keeps "instantly" honest without a FileSystemWatcher —
    // watchers fire mid-write and need debouncing, and the rest of this plugin
    // polls for exactly that reason (see GrantPoller, CatalogSync).
    private const float CheckSeconds = 1f;

    /// The built-in position for an aura: what it had before this file existed.
    /// The template is generated from these, so "default" and "current
    /// behaviour" cannot drift apart.
    public static Vector3 DefaultFor(string auraId)
    {
        if (auraId != null && ArmorVfx.Registry.TryGetValue(auraId, out var def))
            return ArmorVfx.CompanionOffset + new Vector3(0f, def.Raise, 0f);
        return ArmorVfx.CompanionOffset;
    }

    /// Where this familiar hovers. Falls back to the built-in default for any
    /// aura the file doesn't mention, so a half-written file is still usable.
    public static Vector3 OffsetFor(string auraId)
    {
        if (auraId != null && _offsets.TryGetValue(auraId, out var v)) return v;
        return DefaultFor(auraId);
    }

    public static void Load()
    {
        EnsureFile();
        ReadFile(announce: true);
    }

    /// Cheap timestamp check; re-reads only when the file actually changed.
    /// Safe to call every tick.
    public static void ReloadIfChanged()
    {
        if (Time.realtimeSinceStartup < _nextCheck) return;
        _nextCheck = Time.realtimeSinceStartup + CheckSeconds;

        try
        {
            if (!File.Exists(ConfigPath)) return;
            var stamp = File.GetLastWriteTimeUtc(ConfigPath);
            if (stamp == _stamp) return;
            ReadFile(announce: false);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[Valcoin][Familiars] layout check failed: " + ex.Message);
        }
    }

    // ─── reading ──────────────────────────────────────────────────────────

    // Indentation is NOT what tells these apart -- content is. An entry is
    // "<name>:" with nothing after the colon; an axis is "x: <number>". Keying
    // on exact indent instead would silently drop a hand-edited line that used
    // two spaces where the template used four, which is the single most likely
    // thing to happen to a file whose whole purpose is being hand-edited.
    private static readonly Regex SectionRe = new Regex(@"^familiars\s*:\s*$", RegexOptions.Compiled);
    private static readonly Regex EntryRe   = new Regex(@"^\s+([A-Za-z0-9_]+)\s*:\s*$", RegexOptions.Compiled);
    private static readonly Regex AxisRe    = new Regex(@"^\s+([xyzXYZ])\s*:\s*([-+]?(?:\d+\.?\d*|\.\d+))\s*$", RegexOptions.Compiled);

    private static void ReadFile(bool announce)
    {
        try
        {
            // Read the timestamp BEFORE the content. The other order has a race:
            // a write landing between the read and the stamp would be recorded as
            // already-loaded and then ignored until the next edit.
            var stamp = File.GetLastWriteTimeUtc(ConfigPath);
            var lines = File.ReadAllLines(ConfigPath);

            var parsed = new Dictionary<string, Vector3>();
            var unknown = new List<string>();
            bool inSection = false;
            string current = null;
            Vector3 v = Vector3.zero;

            void Commit()
            {
                if (current == null) return;
                if (ArmorVfx.Registry.ContainsKey(current)) parsed[current] = v;
                else unknown.Add(current);
                current = null;
            }

            foreach (var raw in lines)
            {
                var line = raw.TrimEnd();
                if (line.Length == 0 || line.TrimStart().StartsWith("#")) continue;

                if (!inSection)
                {
                    if (SectionRe.IsMatch(line)) inSection = true;
                    continue;
                }

                var entry = EntryRe.Match(line);
                if (entry.Success)
                {
                    Commit();
                    current = entry.Groups[1].Value.ToLowerInvariant();
                    // Seed with the built-in default so an entry that only sets
                    // `y` keeps the shipped x and z instead of snapping to 0.
                    v = DefaultFor(current);
                    continue;
                }

                var axis = AxisRe.Match(line);
                if (axis.Success && current != null)
                {
                    if (float.TryParse(axis.Groups[2].Value, NumberStyles.Float,
                                       CultureInfo.InvariantCulture, out var f))
                    {
                        switch (axis.Groups[1].Value.ToLowerInvariant())
                        {
                            case "x": v.x = f; break;
                            case "y": v.y = f; break;
                            case "z": v.z = f; break;
                        }
                    }
                    continue;
                }

                // A non-indented key ends the section (next top-level block).
                if (Regex.IsMatch(line, @"^\S")) { Commit(); break; }
            }
            Commit();

            _offsets = parsed;
            _stamp = stamp;
            Version++;

            if (unknown.Count > 0)
                Debug.LogWarning($"[Valcoin][Familiars] ignored unknown familiar(s): {string.Join(", ", unknown.ToArray())}");

            if (announce)
                Debug.Log($"[Valcoin][Familiars] layout loaded: {parsed.Count} position(s) from {ConfigPath}");
            else
                Debug.Log($"[Valcoin][Familiars] layout reloaded ({parsed.Count} position(s)).");
        }
        catch (Exception ex)
        {
            // Keep the last good layout. A file caught mid-save should not blank
            // every familiar's position; the next timestamp change retries.
            Debug.LogWarning("[Valcoin][Familiars] layout read failed, keeping previous: " + ex.Message);
        }
    }

    // ─── template ─────────────────────────────────────────────────────────

    private static void EnsureFile()
    {
        try
        {
            Directory.CreateDirectory(Paths.ConfigPath);
            if (File.Exists(ConfigPath)) return;
            File.WriteAllText(ConfigPath, BuildTemplate());
            Debug.Log("[Valcoin][Familiars] wrote default layout to " + ConfigPath);
        }
        catch (Exception ex)
        {
            Debug.LogError("[Valcoin][Familiars] could not write layout template: " + ex.Message);
        }
    }

    // Generated from the registry, not typed out, so the file always ships with
    // the positions the build actually uses.
    private static string BuildTemplate()
    {
        var sb = new StringBuilder();
        sb.Append(
@"# Valcoin — Familiar positions
# ------------------------------------------------------------
# Where each familiar hovers, relative to YOUR character.
#
#   x   left / right   (negative = left, positive = right)
#   y   height         (1.55 is about head height)
#   z   front / back   (positive = in front of you, negative = behind)
#
# Save the file and the change appears in-game within a second — no restart,
# no need to re-equip the helmet. If a familiar is missing from this list it
# uses its built-in position, so you can delete any entry you don't care about.
#
# This is a LOCAL, COSMETIC setting: it changes where familiars sit on YOUR
# screen, including other players'. It is not shared with the server and it
# does not affect prices, perks or your Valcoins.
#
# The values below are the defaults.

familiars:
");
        foreach (var slot in ArmorVfx.Slots)
        {
            foreach (var kv in ArmorVfx.Registry)
            {
                var def = kv.Value;
                if (def.Slot != slot) continue;
                var p = DefaultFor(def.Id);
                sb.AppendLine($"  # {def.Display}");
                sb.AppendLine($"  {def.Id}:");
                sb.AppendLine($"    x: {p.x.ToString("0.##", CultureInfo.InvariantCulture)}");
                sb.AppendLine($"    y: {p.y.ToString("0.##", CultureInfo.InvariantCulture)}");
                sb.AppendLine($"    z: {p.z.ToString("0.##", CultureInfo.InvariantCulture)}");
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }
}
