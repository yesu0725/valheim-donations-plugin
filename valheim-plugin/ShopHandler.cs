using System;
using UnityEngine;

// Orchestrates a shop purchase: validate locally, debit via backend
// (idempotent), then apply the perk-side effect on success.
//
// Effects supported:
//   grant_perk    — flips a passive flag in PerkManager
//   add_charges   — adds N consumable uses
//   grant_item    — spawns item(s) into the world at the buyer's feet, gated by
//                   an optional boss requirement and a backend-enforced weekly cap
public static class ShopHandler
{
    public delegate void TellFn(string msg);

    private class SpendResp
    {
        public string status;   // "ok" | "duplicate"
        public int    balance;
        public int    spent;
    }

    public static void Buy(string steam64, string skuId, TellFn tell, Action onSuccess = null)
    {
        if (string.IsNullOrEmpty(steam64)) { tell("Couldn't resolve your Steam ID."); return; }
        if (!Config.Ready)                 { tell("Shop is offline (backend not configured)."); return; }
        if (!Catalog.Items.TryGetValue(skuId, out var sku))
        { tell($"Unknown SKU: {skuId}. Check the Shop tab for the list."); return; }

        // Cheap local pre-check so we don't make a network call only to bounce.
        int local = CoinManager.GetBalance(steam64);
        if (local < sku.Price)
        { tell($"Not enough Valcoins ({local} / {sku.Price})."); return; }

        // For grant_perk SKUs, refuse re-purchase if they already own it.
        if (sku.Effect == "grant_perk" && PerkManager.Has(steam64, sku.Perk))
        { tell($"You already own \"{sku.Name}\"."); return; }

        // grant_item pre-checks BEFORE any coins are debited, so we never charge
        // a player we then can't deliver to.
        if (sku.Effect == "grant_item")
        {
            if (!string.IsNullOrEmpty(sku.RequiresBoss) && !BossGateSatisfied(sku.RequiresBoss))
            { tell($"\"{sku.Name}\" unlocks after a later boss. Keep progressing!"); return; }

            if (SteamIdResolver.ZdoFor(steam64) == null)
            { tell("Couldn't find your character to deliver items. Spawn in, then try again."); return; }
        }

        // Don't truncate — Substring(0,32) was lopping off the GUID hex,
        // making collisions slightly more likely. Backend caps at 64 chars.
        var key = $"buy-{skuId}-{Guid.NewGuid():N}";
        var body = new
        {
            steam64,
            sku = skuId,
            coins = sku.Price,
            idempotency_key = key,
            // Backend enforces the weekly cap; 0 = unlimited (perk SKUs, etc.).
            weekly_cap = sku.Effect == "grant_item" ? sku.WeeklyCap : 0,
            // add_charges SKUs credit a backend-tracked charge pool in the same
            // spend tx (null for other effects so validation ignores them).
            grant_charges = sku.Effect == "add_charges" ? (int?)sku.Charges : null,
            charge_kind   = sku.Effect == "add_charges" ? sku.Perk : null,
        };

        SharedCoroutineRunner.Instance.StartCoroutine(BackendClient.Post<SpendResp>(
            "/api/spend", body, (ok, r, err) =>
            {
                if (!ok || r == null)
                {
                    if (err != null && err.Contains("429"))
                        tell($"Weekly limit reached for \"{sku.Name}\". {ExtractDetail(err)}".TrimEnd());
                    else if (err != null && err.Contains("402"))
                        tell("The server says you don't have enough Valcoins. Check your balance at the top of the panel.");
                    else
                        tell($"Purchase failed. ({err ?? "unknown"})");
                    return;
                }

                // Sync local cache to authoritative balance.
                CoinManager.SetBalance(steam64, r.balance);

                // A "duplicate" means the debit already happened on an earlier
                // attempt (network retry). Don't apply the effect a second time
                // — that would hand out free items / charges.
                if (r.status == "duplicate")
                {
                    tell($"\"{sku.Name}\" was already processed. Balance: {r.balance}.");
                    onSuccess?.Invoke();
                    return;
                }

                // Apply the effect locally.
                ApplyEffect(steam64, sku, tell);
                onSuccess?.Invoke();
            }));
    }

    private static void ApplyEffect(string steam64, Catalog.Sku sku, TellFn tell)
    {
        switch (sku.Effect)
        {
            case "grant_perk":
                PerkManager.Grant(steam64, sku.Perk);
                tell($"Purchased \"{sku.Name}\" - perk \"{sku.Perk}\" unlocked!");
                break;

            case "add_charges":
                // Charges are credited backend-side during /api/spend (see Buy);
                // the client just refreshes state to see the new count.
                tell($"Purchased \"{sku.Name}\" - +{sku.Charges} charge(s) added.");
                break;

            case "grant_item":
                int delivered = GrantItems(steam64, sku.Item);
                if (delivered > 0)
                    tell($"Purchased \"{sku.Name}\" - {delivered} item stack(s) dropped at your feet.");
                else
                    tell($"\"{sku.Name}\" was charged but no items could be spawned (bad prefab id?). Tell an admin.");
                break;

            default:
                Debug.LogWarning($"[Valcoin] Unknown effect type \"{sku.Effect}\" for SKU {sku.Id}");
                tell($"\"{sku.Name}\" was charged but the effect couldn't be applied. Tell an admin.");
                break;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // grant_item helpers
    // ─────────────────────────────────────────────────────────────────────

    // Spawns the comma-separated item spec as ItemDrops at the buyer's feet.
    // Returns the number of item stacks actually spawned. Spawning into the
    // world (rather than writing a remote player's inventory) is server-
    // authoritative and works for vanilla clients too.
    private static int GrantItems(string steam64, string itemSpec)
    {
        if (string.IsNullOrEmpty(itemSpec)) return 0;
        if (ZNetScene.instance == null) { Debug.LogWarning("[Valcoin] grant_item: no ZNetScene."); return 0; }

        var zdo = SteamIdResolver.ZdoFor(steam64);
        if (zdo == null) { Debug.LogWarning("[Valcoin] grant_item: no ZDO for buyer."); return 0; }
        Vector3 basePos = zdo.GetPosition();

        int stacks = 0;
        foreach (var raw in itemSpec.Split(','))
        {
            var piece = raw.Trim();
            if (piece.Length == 0) continue;

            string prefabName = piece;
            int qty = 1;
            int colon = piece.LastIndexOf(':');
            if (colon > 0)
            {
                prefabName = piece.Substring(0, colon).Trim();
                if (!int.TryParse(piece.Substring(colon + 1), out qty) || qty < 1) qty = 1;
            }

            var prefab = ZNetScene.instance.GetPrefab(prefabName);
            if (prefab == null)
            {
                Debug.LogWarning($"[Valcoin] grant_item: unknown prefab \"{prefabName}\" — skipped.");
                continue;
            }

            var proto = prefab.GetComponent<ItemDrop>();
            int maxStack = (proto != null && proto.m_itemData?.m_shared != null)
                ? Mathf.Max(1, proto.m_itemData.m_shared.m_maxStackSize)
                : 1;

            int remaining = qty;
            while (remaining > 0)
            {
                int thisStack = Mathf.Min(remaining, maxStack);
                Vector3 pos = basePos + new Vector3(
                    UnityEngine.Random.Range(-1.0f, 1.0f), 1.5f, UnityEngine.Random.Range(-1.0f, 1.0f));

                try
                {
                    // Server-side world spawn (the buyer may be a remote client, so
                    // there's no local inventory to write). Setting m_stack right
                    // after Instantiate is the same pattern ServerGuide's reward
                    // dispatcher uses for its inventory-full drop fallback.
                    var go = UnityEngine.Object.Instantiate(prefab, pos, Quaternion.identity);
                    var drop = go.GetComponent<ItemDrop>();
                    if (drop != null)
                        drop.m_itemData.m_stack = thisStack;
                    stacks++;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[Valcoin] grant_item: failed to spawn {prefabName}: {ex.Message}");
                }

                remaining -= thisStack;
            }
        }
        return stacks;
    }

    // True if the boss global key is set (world progression gate). Fails open
    // (allows the purchase) if the key system can't be read, since gating is a
    // balance nicety, not a security control — better than blocking a paid buy.
    private static bool BossGateSatisfied(string bossKey)
    {
        if (string.IsNullOrEmpty(bossKey)) return true;
        try
        {
            if (ZoneSystem.instance == null) return true;
            // GetGlobalKey(string) -> bool is the stable boss-defeat check the
            // sibling mods use (e.g. BiomeLords' LordDefeatStore, ServerGuide's
            // SeenTracker). Keys look like "defeated_bonemass".
            return ZoneSystem.instance.GetGlobalKey(bossKey);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Valcoin] boss-gate check failed for \"{bossKey}\": {ex.Message}");
            return true;
        }
    }

    // Pulls the human-readable "detail" out of a FastAPI error body embedded in
    // the BackendClient error string, e.g. {"detail":"...; resets in 3d 4h"}.
    private static string ExtractDetail(string err)
    {
        if (string.IsNullOrEmpty(err)) return "";
        const string marker = "\"detail\":\"";
        int i = err.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return "";
        i += marker.Length;
        int j = err.IndexOf('"', i);
        return j > i ? err.Substring(i, j - i) : "";
    }
}
