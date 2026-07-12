using BepInEx;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using UnityEngine;

// Loads BepInEx/config/valcoin_shop.yaml — operator-editable shop catalog.
// We keep the format dead simple so we don't need YamlDotNet:
//
//   shop:
//     donor_badge:                 # grant_perk — cosmetic flag
//       name: "Donor Badge"
//       description: "A ⭐ next to your name in chat"
//       price: 500
//       effect: grant_perk
//       perk: donor_badge
//
//     food_t2:                     # grant_item — weekly-limited consumable
//       name: "Plains Feast (bundle)"
//       price: 180
//       effect: grant_item
//       item: "LoxPie:5,Bread:5,FishWraps:5"   # comma list of prefab or prefab:qty
//       weekly_cap: 3              # max purchases per player per week (0 = unlimited)
//       requires_boss: defeated_goblinking     # global boss key gate (optional)
//
// A full ecosystem-aware catalog lives in examples/valcoin_shop.example.yaml.
//
public static class Catalog
{
    public class Sku
    {
        public string Id;            // map key
        public string Name;          // human label
        public string Description;
        public int    Price;
        public string Effect;        // grant_perk | add_charges | grant_item
        public string Perk;          // identifier the PerkManager understands (perk effects)
        public int    Charges = 1;   // for add_charges effect
        public string Item;          // grant_item: comma list of "prefab" or "prefab:qty"
        public int    WeeklyCap;     // grant_item: max purchases per player per week (0 = unlimited)
        public string RequiresBoss;  // grant_item: global boss key gate, e.g. defeated_bonemass (optional)
    }

    public static Dictionary<string, Sku> Items { get; private set; } = new Dictionary<string, Sku>();
    public static List<Sku>               Order { get; private set; } = new List<Sku>();

    private static readonly string CatalogPath = Path.Combine(Paths.ConfigPath, "valcoin_shop.yaml");

    public static void Load()
    {
        EnsureFile();
        try
        {
            var lines = File.ReadAllLines(CatalogPath);
            Parse(lines);
            Debug.Log($"[Valcoin] Shop catalog loaded: {Items.Count} SKU(s).");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Valcoin] Failed to parse shop catalog: {ex.Message}");
            Items = new Dictionary<string, Sku>();
            Order = new List<Sku>();
        }
    }

    private static void EnsureFile()
    {
        if (File.Exists(CatalogPath)) return;
        try
        {
            File.WriteAllText(CatalogPath,
@"# Valcoin shop catalog
# -----------------------------------------------------------------------
# Each SKU has:
#   name:          what shows in the Shop tab (F8 panel / F4 Codex)
#   description:   helper text
#   price:         Valcoin cost
#   effect:        grant_perk | add_charges | grant_item
#   perk:          (perk effects) identifier the plugin understands
#   charges:       (add_charges) how many uses each purchase grants
#   item:          (grant_item) comma list of ""prefab"" or ""prefab:qty""
#   weekly_cap:    (grant_item) max purchases per player per week (0 = unlimited)
#   requires_boss: (grant_item) global boss key gate, e.g. defeated_bonemass
# A full ecosystem-aware catalog is in examples/valcoin_shop.example.yaml.
# Edit and restart the server to apply changes.

shop:

  # ---------- Convenience: Soulkeeper Charm (death insurance) ----------
  # Charges of one shared 'soulkeeper' pool. On death you keep your skills (no
  # skill drain). Never helps you win a fight - it only softens the death tax.
  soulkeeper_1:
    name: ""Soulkeeper Charm (x1)""
    description: ""On death, keep your skills - no skill drain. Adds 1 charge.""
    price: 300
    effect: add_charges
    perk: soulkeeper
    charges: 1

  soulkeeper_5:
    name: ""Soulkeeper Charm (x5)""
    description: ""On death, keep your skills - no skill drain. Adds 5 charges.""
    price: 1200
    effect: add_charges
    perk: soulkeeper
    charges: 5

  soulkeeper_10:
    name: ""Soulkeeper Charm (x10)""
    description: ""On death, keep your skills - no skill drain. Adds 10 charges. Best value.""
    price: 1300
    effect: add_charges
    perk: soulkeeper
    charges: 10

  # ---------- Weekly-limited food (progression-gated) ----------
  food_t1:
    name: ""Swamp Feast (bundle)""
    description: ""Sausages + Blood Pudding + Serpent Stew, x5 each. Requires Bonemass.""
    price: 120
    effect: grant_item
    item: ""Sausages:5,BloodPudding:5,SerpentStew:5""
    weekly_cap: 4
    requires_boss: defeated_bonemass

  food_t2:
    name: ""Plains Feast (bundle)""
    description: ""Lox Meat Pie + Bread + Fish Wraps, x5 each. Requires Yagluth.""
    price: 180
    effect: grant_item
    item: ""LoxPie:5,Bread:5,FishWraps:5""
    weekly_cap: 3
    requires_boss: defeated_goblinking

  food_t3:
    name: ""Mistlands Feast (bundle)""
    description: ""Misthare Supreme + Mushroom Omelette + Yggdrasil Porridge, x5 each. Requires the Queen.""
    price: 260
    effect: grant_item
    item: ""MisthareSupreme:5,MushroomOmelette:5,YggdrasilPorridge:5""
    weekly_cap: 2
    requires_boss: defeated_queen

  food_t4:
    name: ""Ashlands Feast (bundle)""
    description: ""Mashed Meat + Piquant Pie + Marinated Greens, x5 each. Requires the Ashlands boss.""
    price: 350
    effect: grant_item
    item: ""MashedMeat:5,PiquantPie:5,MarinatedGreens:5""
    weekly_cap: 2
    requires_boss: defeated_fader

  # ---------- Weekly-limited meads ----------
  meads_utility:
    name: ""Utility Meads (bundle)""
    description: ""Tasty + Frost Resistance + Poison Resistance mead, x5 each.""
    price: 100
    effect: grant_item
    item: ""MeadTasty:5,MeadFrostResist:5,MeadPoisonResist:5""
    weekly_cap: 3

  meads_vitality:
    name: ""Vitality Meads (bundle)""
    description: ""Medium Healing + Medium Stamina mead, x5 each. Requires Bonemass.""
    price: 160
    effect: grant_item
    item: ""MeadHealthMedium:5,MeadStaminaMedium:5""
    weekly_cap: 2
    requires_boss: defeated_bonemass

  meads_eitr:
    name: ""Eitr Meads (bundle)""
    description: ""Minor Eitr mead, x5. Requires the Queen.""
    price: 160
    effect: grant_item
    item: ""MeadEitrMinor:5""
    weekly_cap: 2
    requires_boss: defeated_queen

  # ---------- Weekly-limited materials ----------
  farm_bundle:
    name: ""Farmer's Crate""
    description: ""Barley + Flax + Onion/Carrot/Turnip seeds, x20 each. Requires Yagluth (barley/flax).""
    price: 120
    effect: grant_item
    item: ""Barley:20,Flax:20,OnionSeeds:20,CarrotSeeds:20,TurnipSeeds:20""
    weekly_cap: 2
    requires_boss: defeated_goblinking

  forage_bundle:
    name: ""Forager's Crate""
    description: ""Coal x50, Resin x50, Feathers x50, Thistle/Dandelion/Honey x20. No gate.""
    price: 100
    effect: grant_item
    item: ""Coal:50,Resin:50,Feathers:50,Thistle:20,Dandelion:20,Honey:20""
    weekly_cap: 2
");
            Debug.LogWarning($"[Valcoin] Created shop catalog template at {CatalogPath}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Valcoin] Could not write catalog template: {ex.Message}");
        }
    }

    // Tiny indent-aware parser. Recognises:
    //   shop:
    //     <id>:
    //       <field>: <value>
    //
    // Values can be plain numbers, plain words, or "double-quoted strings".
    private static readonly Regex KvRe   = new Regex(@"^\s*([a-zA-Z_]+)\s*:\s*(.*)$", RegexOptions.Compiled);
    private static readonly Regex SkuRe  = new Regex(@"^  ([a-z0-9_]+)\s*:\s*$",       RegexOptions.Compiled);
    private static readonly Regex FieldRe = new Regex(@"^    ([a-zA-Z_]+)\s*:\s*(.*)$", RegexOptions.Compiled);

    private static void Parse(string[] lines)
    {
        var items = new Dictionary<string, Sku>();
        var order = new List<Sku>();

        bool inShop = false;
        Sku current = null;

        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var trimmed = raw.TrimStart();
            if (trimmed.StartsWith("#")) continue;

            if (!inShop)
            {
                if (Regex.IsMatch(raw, @"^shop\s*:\s*$"))
                    inShop = true;
                continue;
            }

            var skuMatch = SkuRe.Match(raw);
            if (skuMatch.Success)
            {
                if (current != null) Commit(current, items, order);
                current = new Sku { Id = skuMatch.Groups[1].Value };
                continue;
            }

            var fieldMatch = FieldRe.Match(raw);
            if (current != null && fieldMatch.Success)
            {
                var key = fieldMatch.Groups[1].Value;
                var val = StripQuotes(fieldMatch.Groups[2].Value.Trim());

                switch (key)
                {
                    case "name":          current.Name = val; break;
                    case "description":   current.Description = val; break;
                    case "price":         int.TryParse(val, out current.Price); break;
                    case "effect":        current.Effect = val; break;
                    case "perk":          current.Perk = val; break;
                    case "charges":       int.TryParse(val, out current.Charges); break;
                    case "item":          current.Item = val; break;
                    case "weekly_cap":    int.TryParse(val, out current.WeeklyCap); break;
                    case "requires_boss": current.RequiresBoss = val; break;
                }
                continue;
            }

            // A non-indented top-level key after `shop:` ends the block.
            if (raw.Length > 0 && raw[0] != ' ' && KvRe.IsMatch(raw))
                break;
        }

        if (current != null) Commit(current, items, order);

        Items = items;
        Order = order;
    }

    private static void Commit(Sku s, Dictionary<string, Sku> items, List<Sku> order)
    {
        if (string.IsNullOrEmpty(s.Id) || string.IsNullOrEmpty(s.Effect))
            return;

        // grant_item SKUs carry `item` (and no `perk`); perk-based effects
        // (grant_perk / add_charges) carry `perk`. Require the field that
        // matches the effect so a malformed SKU is dropped rather than
        // silently charging a player for nothing.
        if (s.Effect == "grant_item")
        {
            if (string.IsNullOrEmpty(s.Item)) return;
        }
        else if (string.IsNullOrEmpty(s.Perk))
        {
            return;
        }

        if (string.IsNullOrEmpty(s.Name)) s.Name = s.Id;
        items[s.Id] = s;
        order.Add(s);
    }

    // ─── remote sync (Phase 3) ──────────────────────────────────────────────
    //
    // valcoin_shop.yaml only exists on whichever machine loaded it (the
    // dedicated server, or a listen-server host). A remote client connecting
    // to a dedicated server has no such file, so its own Catalog.Load() sees
    // nothing. CatalogSync broadcasts the server's parsed catalog over RPC;
    // remote clients apply it here, in memory only — never written to disk,
    // so it can't stomp a local file and always reflects the server's truth.

    public static string Serialize()
    {
        try { return JsonConvert.SerializeObject(Order); }
        catch (Exception ex)
        {
            Debug.LogError($"[Valcoin] Catalog serialize failed: {ex.Message}");
            return null;
        }
    }

    public static void ApplyRemote(string json)
    {
        if (string.IsNullOrEmpty(json)) return;
        try
        {
            var order = JsonConvert.DeserializeObject<List<Sku>>(json);
            if (order == null) return;

            var items = new Dictionary<string, Sku>();
            foreach (var s in order)
                if (!string.IsNullOrEmpty(s.Id)) items[s.Id] = s;

            Items = items;
            Order = order;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Valcoin] Catalog ApplyRemote failed: {ex.Message}");
        }
    }

    private static string StripQuotes(string v)
    {
        if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"')
            return v.Substring(1, v.Length - 2);
        return v;
    }
}
