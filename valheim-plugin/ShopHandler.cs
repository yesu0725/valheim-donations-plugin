using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Orchestrates a shop purchase: validate locally, debit via backend
// (idempotent, retried on an unknown outcome), apply the perk-side effect,
// and REFUND if that effect could not be delivered. A player must never end
// up short a Valcoin for something they did not receive.
//
// Effects supported:
//   grant_perk    — flips a passive flag in PerkManager
//   add_charges   — adds N consumable uses
//   grant_item    — spawns item(s) into the world at the buyer's feet, gated by
//                   an optional boss requirement and a backend-enforced weekly cap
public static class ShopHandler
{
    public delegate void TellFn(string msg);

    // Result wire format back to the in-game panel: the human sentence, then a
    // bare verdict marker.
    //
    //   <the message>
    //   "__BUYRES__:ok"      the debit landed AND the effect was delivered
    //   "__BUYRES__:fail"    refused, or charged and then refunded -- either way
    //                        the player is not out any Valcoins
    //   "__BUYRES__:hold"    we could not establish what happened
    //
    // The panel used to infer this by string-matching the reply ("does it start
    // with 'Purchased'?"), which mislabelled every wording it did not know --
    // including "was charged but no items could be spawned", the one case where
    // announcing a failure while keeping the coins is worst. The server knows the
    // answer, so it now says it outright.
    //
    // WHY THE MESSAGE COMES FIRST, AND WHY THE MARKER CARRIES NO TEXT. A panel
    // older than 5.22.0 treats the next plain line as the verdict and knows
    // nothing about markers. Sending the sentence first means such a client
    // consumes it exactly as it always did (and the marker afterwards is a stray
    // log line, not a modal full of wire format). Sending it as a SEPARATE line
    // rather than as the marker's payload is what keeps that true. Servers and
    // clients are updated independently here -- a Thunderstore client is on
    // 5.20.0 while this server may be on anything -- so both directions of that
    // skew have to work.
    public const string ResultPrefix = "__BUYRES__:";

    // One initial attempt plus one retry. /api/spend is idempotency-keyed, so
    // retrying with the SAME key can never double-charge: the backend either
    // commits once or answers "duplicate". Two attempts keep the worst case
    // inside the panel's wait window (2 x BackendClient's 15s timeout + the gap).
    private const int   SpendAttempts     = 2;
    private const float RetryDelaySeconds = 2f;

    // A refund matters more than a fast answer, so it gets more tries.
    private const int RefundAttempts = 4;

    private class SpendResp
    {
        public string status;   // "ok" | "duplicate"
        public int    balance;
        public int    spent;
    }

    private class GrantResp
    {
        public string status;
        public long   grant_id;
        public int    coins;
    }

    public static void Buy(string steam64, string skuId, TellFn tell, Action onSuccess = null, string extra = null)
    {
        // Everything in this block runs BEFORE any coins move, so every one of
        // these exits is a clean "fail": nothing was charged.
        if (string.IsNullOrEmpty(steam64)) { Fail(tell, "Couldn't resolve your Steam ID."); return; }
        if (!Config.Ready)                 { Fail(tell, "Shop is offline (backend not configured)."); return; }
        if (!Catalog.Items.TryGetValue(skuId, out var sku))
        { Fail(tell, $"Unknown SKU: {skuId}. Check the Shop tab for the list."); return; }

        // NO local balance pre-check. There used to be one here, reading
        // CoinManager, and it refused purchases players could easily afford.
        //
        // CoinManager is a CACHE, not a ledger. It only ever learns a number by
        // accumulating deltas onto whatever it already held, and it holds 0 for a
        // player it has never seen (CoinManager.GetBalance) — so the first grant
        // recorded for an unknown player MANUFACTURES a balance equal to just that
        // grant. A 2-coin daily quest made the cache believe the player had exactly
        // 2 Valcoins, and this check then refused a purchase against the 17,000 they
        // actually held on the backend. It also never learned about coins credited
        // any other way, so it drifted further from the truth every day.
        //
        // 5.19.2 fixed the "unknown player reads as broke" half of this by only
        // vetoing on a KNOWN balance. That wasn't enough: a known balance is just as
        // wrong once the cache has drifted, and it is more dangerous, because it
        // looks authoritative. The backend owns the ledger and answers 402 on a
        // genuine overdraft, so the decision belongs there and nowhere else. Saving
        // one HTTP round-trip was never worth refusing a player their own money.

        // For grant_perk SKUs, refuse re-purchase if they already own it.
        if (sku.Effect == "grant_perk" && PerkManager.Has(steam64, sku.Perk))
        { Fail(tell, $"You already own \"{sku.Name}\"."); return; }

        // armor_vfx: cosmetic aura applied on the buyer's client. Each aura is
        // bound to one slot (registry), so no slot argument is needed; the
        // visual/rename happen client-side via the __ARMORVFX__ signal.
        if (sku.Effect == "armor_vfx" && ArmorVfx.SlotFor(sku.Perk) == null)
        { Fail(tell, $"\"{sku.Name}\" is misconfigured (unknown effect). Tell an admin."); return; }

        // grant_item pre-checks BEFORE any coins are debited, so we never charge
        // a player we then can't deliver to.
        if (sku.Effect == "grant_item")
        {
            if (!string.IsNullOrEmpty(sku.RequiresBoss) && !BossGateSatisfied(sku.RequiresBoss))
            { Fail(tell, $"\"{sku.Name}\" unlocks after a later boss. Keep progressing!"); return; }

            if (SteamIdResolver.ZdoFor(steam64) == null)
            { Fail(tell, "Couldn't find your character to deliver items. Spawn in, then try again."); return; }
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
            // spend tx (null for other effects so validation ignores them). The
            // weekly charge cap is shared across every SKU of the same kind —
            // the backend sums charges granted this week, not purchase counts.
            grant_charges = sku.Effect == "add_charges" ? (int?)sku.Charges : null,
            charge_kind   = sku.Effect == "add_charges" ? sku.Perk : null,
            weekly_charge_cap = sku.Effect == "add_charges" && sku.WeeklyChargeCap > 0
                ? (int?)sku.WeeklyChargeCap : null,
        };

        SharedCoroutineRunner.Instance.StartCoroutine(
            BuyRoutine(steam64, sku, body, tell, onSuccess, extra));
    }

    // The whole purchase, start to finish, as one coroutine so the retry and the
    // compensating refund can be sequenced against the debit.
    //
    // THE BUG THIS EXISTS TO KILL. Players saw "Purchase failed" and were charged
    // anyway. Three separate paths produced that, and all three are closed here:
    //
    //   1. The reply never came back. The debit is committed the moment the
    //      backend answers 200; if that answer is lost — a dropped connection, or
    //      a Fly.io machine that took longer to wake than BackendClient's 15s
    //      timeout — the old code reported a flat failure and walked away from a
    //      spend that had already happened. Now the request is RETRIED with the
    //      same idempotency key, which is the only way to find out what really
    //      happened, and the answer is authoritative either way.
    //
    //   2. "duplicate" was treated as "somebody else already did this", and the
    //      effect was deliberately NOT applied. That reading is impossible: the
    //      key is a fresh GUID minted for this one call and shared with nobody, so
    //      the only request that can ever have used it is an earlier attempt of
    //      THIS purchase — an attempt whose reply we never saw, and whose effect
    //      we therefore never applied. Duplicate now means "your money is already
    //      spent, deliver the goods", which is what it always meant.
    //
    //   3. The effect itself failed after the debit (no prefab, unknown effect
    //      type). The player was told, truthfully, that they had been charged for
    //      nothing. Now that case REFUNDS.
    private static IEnumerator BuyRoutine(string steam64, Catalog.Sku sku, object body,
                                          TellFn tell, Action onSuccess, string extra)
    {
        bool ok = false; SpendResp resp = null; string err = null;
        yield return BackendClient.PostWithRetry<SpendResp>("/api/spend", body, SpendAttempts,
            RetryDelaySeconds, (o, r, e) => { ok = o; resp = r; err = e; });

        if (!ok || resp == null)
        {
            if (err != null && err.Contains("429"))
                Fail(tell, $"Weekly limit reached for \"{sku.Name}\". {ExtractDetail(err)}".TrimEnd());
            else if (err != null && err.Contains("402"))
            {
                // Prefer the backend's own words — its detail carries the
                // real balance, which is the number the player should act on.
                var detail = ExtractDetail(err);
                Fail(tell, string.IsNullOrEmpty(detail)
                    ? "Not enough Valcoins for that purchase."
                    : $"Not enough Valcoins. {detail}");
            }
            else if (BackendClient.IsDefiniteRefusal(err))
            {
                // A 4xx is the backend saying no. Nothing was charged: /api/spend
                // does its whole debit inside one transaction that rolls back on
                // any error.
                Fail(tell, $"\"{sku.Name}\" was refused. No Valcoins were taken. ({err ?? "unknown"})");
            }
            else
            {
                // Retries exhausted without ever reaching the ledger. We genuinely
                // do not know whether the debit landed, so do not claim it didn't.
                Debug.LogError($"[Valcoin] spend for {sku.Id} by {steam64} unresolved after "
                               + $"{SpendAttempts} attempts: {err}");
                Hold(tell, $"Couldn't reach the Valcoin service to confirm \"{sku.Name}\". "
                           + "Check your balance in a moment before trying again - if it went "
                           + "down without the item arriving, tell an admin and it will be put right.");
            }
            yield break;
        }

        // Sync local cache to authoritative balance.
        CoinManager.SetBalance(steam64, resp.balance);

        // "ok" and "duplicate" are the same thing from here: the coins for THIS
        // key are spent exactly once, and the effect has not been applied yet.
        // See point 2 in the comment above this method.
        string outcome = ApplyEffect(steam64, sku, tell, extra, out bool delivered);

        if (!delivered)
        {
            yield return RefundRoutine(steam64, sku, outcome, tell);
            yield break;
        }

        Ok(tell, outcome);
        onSuccess?.Invoke();
    }

    // Compensating credit for a purchase that was debited and then could not be
    // delivered. /api/admin/grant bumps players.total_coins inside the same
    // transaction that writes the grant row, so the balance is whole again the
    // moment this returns; the grant row is what puts the "+N Valcoins" toast in
    // front of the player and leaves the correction in the ledger where an
    // operator can see it.
    private static IEnumerator RefundRoutine(string steam64, Catalog.Sku sku, string reason, TellFn tell)
    {
        bool ok = false; string err = null;
        yield return BackendClient.PostWithRetry<GrantResp>(
            "/api/admin/grant",
            new { steam64, coins = sku.Price, note = $"refund: {sku.Id} could not be delivered" },
            RefundAttempts, RetryDelaySeconds,
            (o, r, e) => { ok = o; err = e; });

        if (ok)
        {
            Debug.LogWarning($"[Valcoin] refunded {sku.Price} to {steam64} for undelivered {sku.Id}: {reason}");
            Fail(tell, $"{reason} Your {sku.Price} Valcoins have been refunded.");
            yield break;
        }

        // The one case we cannot fix ourselves. Say so precisely enough that an
        // admin can settle it from the ledger without a guessing game.
        Debug.LogError($"[Valcoin] REFUND FAILED: {sku.Price} owed to {steam64} for {sku.Id} "
                       + $"({reason}) — {err}");
        Hold(tell, $"{reason} The automatic refund of {sku.Price} Valcoins did not go through. "
                   + $"Tell an admin and quote \"{sku.Id}\" - the spend is in the ledger and can be reversed.");
    }

    // ─── replies ──────────────────────────────────────────────────────────

    private static void Ok(TellFn tell, string msg)   => Verdict(tell, "ok", msg);
    private static void Fail(TellFn tell, string msg) => Verdict(tell, "fail", msg);
    private static void Hold(TellFn tell, string msg) => Verdict(tell, "hold", msg);

    // Message first, marker second — see the note on ResultPrefix.
    private static void Verdict(TellFn tell, string kind, string msg)
    {
        tell(msg);
        tell(ResultPrefix + kind);
    }

    // ─── effects ──────────────────────────────────────────────────────────

    // Applies the SKU's effect and reports what happened. `delivered` false means
    // the coins bought nothing and the caller must refund — the returned string is
    // then the reason, written so it reads correctly with " Your N Valcoins have
    // been refunded." appended.
    private static string ApplyEffect(string steam64, Catalog.Sku sku, TellFn tell,
                                      string extra, out bool delivered)
    {
        delivered = true;
        switch (sku.Effect)
        {
            case "armor_vfx":
                // The visual + rename happen on the buyer's client. Signal it
                // with a control message the panel intercepts (__ARMORVFX__),
                // carrying the SKU id so the client can ask for a refund if the
                // apply fails on its side (see ReportApplyFailed). Slot comes from
                // the aura registry (each aura is bound to one armor slot).
                string slot = ArmorVfx.SlotFor(sku.Perk);
                ArmVfxRefund(steam64, sku);
                tell("__ARMORVFX__:" + sku.Perk + ":" + slot + ":" + sku.Id);
                return $"Purchased \"{sku.Name}\" - applying to your {slot} armor...";

            case "grant_perk":
                PerkManager.Grant(steam64, sku.Perk);
                return $"Purchased \"{sku.Name}\" - perk \"{sku.Perk}\" unlocked!";

            case "add_charges":
                // Charges are credited backend-side during /api/spend (see Buy);
                // the client refreshes state to see the new count, so it can take
                // a few seconds to appear — say so to set the expectation.
                return $"Purchased \"{sku.Name}\" - +{sku.Charges} charge(s). "
                       + "It may take a few seconds for your charge count to update.";

            case "grant_item":
                int spawned = GrantItems(steam64, sku.Item);
                if (spawned > 0)
                    return $"Purchased \"{sku.Name}\" - {spawned} item stack(s) dropped at your feet.";
                delivered = false;
                return $"\"{sku.Name}\" couldn't be delivered - no items could be spawned (bad prefab id?).";

            default:
                Debug.LogWarning($"[Valcoin] Unknown effect type \"{sku.Effect}\" for SKU {sku.Id}");
                delivered = false;
                return $"\"{sku.Name}\" couldn't be delivered - the server doesn't know the effect "
                       + $"\"{sku.Effect}\". Tell an admin.";
        }
    }

    // ─── armor_vfx refund tickets ─────────────────────────────────────────
    //
    // An armor aura is the one effect only the BUYER'S CLIENT can apply: it binds
    // to the helmet they have equipped, and the server can't see that. If they
    // unequip between confirming and the reply arriving, the apply fails on the
    // client and the coins are already gone.
    //
    // The client reports that with a "buyfail:<sku>" action — but a client-driven
    // refund is a client-driven way to print money unless it is bounded. So each
    // armor_vfx purchase mints a ONE-SHOT ticket here: the refund is honoured only
    // against a ticket that exists, has not been spent, and has not expired.
    // Without a real purchase behind it, a spoofed "buyfail" refunds nothing.

    private class VfxTicket
    {
        public Catalog.Sku Sku;
        public float ExpiresAt;
    }

    private const float VfxTicketSeconds = 90f;
    private static readonly Dictionary<string, VfxTicket> _vfxTickets = new Dictionary<string, VfxTicket>();

    private static string VfxKey(string steam64, string skuId) => steam64 + "|" + skuId;

    private static void ArmVfxRefund(string steam64, Catalog.Sku sku)
    {
        float now = Time.realtimeSinceStartup;
        var stale = new List<string>();
        foreach (var kv in _vfxTickets)
            if (kv.Value.ExpiresAt < now) stale.Add(kv.Key);
        foreach (var k in stale) _vfxTickets.Remove(k);

        _vfxTickets[VfxKey(steam64, sku.Id)] = new VfxTicket { Sku = sku, ExpiresAt = now + VfxTicketSeconds };
    }

    // "buyfail:<sku>" from the panel: the client couldn't apply an armor aura it
    // just paid for. Consumes the ticket and refunds.
    public static void ReportApplyFailed(string steam64, string skuId, TellFn tell)
    {
        if (string.IsNullOrEmpty(steam64) || string.IsNullOrEmpty(skuId)) return;

        string key = VfxKey(steam64, skuId);
        if (!_vfxTickets.TryGetValue(key, out var ticket) || ticket.ExpiresAt < Time.realtimeSinceStartup)
        {
            _vfxTickets.Remove(key);
            Debug.LogWarning($"[Valcoin] buyfail for {skuId} from {steam64} with no live ticket — ignored.");
            return;
        }
        _vfxTickets.Remove(key);   // one shot, consumed whether or not the refund lands

        SharedCoroutineRunner.Instance.StartCoroutine(RefundRoutine(
            steam64, ticket.Sku,
            $"\"{ticket.Sku.Name}\" couldn't be applied - you had no armor equipped in that slot.",
            tell));
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
