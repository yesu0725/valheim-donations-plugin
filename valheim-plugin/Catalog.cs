using BepInEx;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
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
#   name:          what shows in /shop
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
  donor_badge:
    name: ""Donor Badge""
    description: ""A star next to your name in chat. Forever.""
    price: 500
    effect: grant_perk
    perk: donor_badge

  chat_title:
    name: ""Chat Title""
    description: ""Unlocks /title <name> to set a custom prefix (e.g. [Jarl])""
    price: 1500
    effect: grant_perk
    perk: chat_title

  companion_flair:
    name: ""Patron's Companion Flair""
    description: ""A donor-only badge colour and name style on your Dvergr companions. Cosmetic only.""
    price: 2000
    effect: grant_perk
    perk: companion_flair
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

    private static string StripQuotes(string v)
    {
        if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"')
            return v.Substring(1, v.Length - 2);
        return v;
    }
}
