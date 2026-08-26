using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Replaces the FTP-drop CoinQueueHandler. Polls the backend for undelivered
// grants over HTTPS, applies them via CoinManager, and acks back so they don't
// get re-delivered.
public class GrantPoller : MonoBehaviour
{
    public class Grant
    {
        public long   id;
        public string steam64;
        public int    coins;
        public string source;
        public string note;
        public string created_at;
    }

    private class PendingResponse { public List<Grant> grants; }
    private class AckRequest      { public List<long>  ids; }
    private class AckResponse     { public int         acked; }
    private class StateResponse   { public int         balance; }

    private Coroutine _loop;

    private void Start()
    {
        _loop = StartCoroutine(Loop());
    }

    private void OnDestroy()
    {
        if (_loop != null) StopCoroutine(_loop);
    }

    private IEnumerator Loop()
    {
        // Don't spam the API while ZNet is still bootstrapping.
        while (ZNet.instance == null || !ZNet.instance.IsServer())
            yield return new WaitForSeconds(2f);

        while (true)
        {
            yield return new WaitForSeconds(Mathf.Max(2f, Config.PollIntervalSeconds));
            if (!Config.Ready) continue;
            yield return Tick();
        }
    }

    private IEnumerator Tick()
    {
        PendingResponse pending = null;
        string err = null;
        yield return BackendClient.Get<PendingResponse>("/api/grants/pending?limit=50", (ok, r, e) =>
        {
            if (ok) pending = r; else err = e;
        });

        if (err != null)
        {
            Debug.LogWarning($"[Valcoin] poll failed: {err}");
            yield break;
        }
        if (pending?.grants == null || pending.grants.Count == 0)
            yield break;

        var applied = new List<long>(pending.grants.Count);
        var toToast = new List<Grant>(pending.grants.Count);
        var touched = new List<string>();

        foreach (var g in pending.grants)
        {
            try
            {
                if (CoinManager.TryApplyGrant(g.id, g.steam64, g.coins))
                    toToast.Add(g);
                else
                    Debug.Log($"[Valcoin] grant {g.id} replay (already applied locally); will re-ack.");

                applied.Add(g.id);
                if (!touched.Contains(g.steam64)) touched.Add(g.steam64);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[Valcoin] failed to apply grant {g.id}: {ex.Message}");
            }
        }

        if (applied.Count > 0)
        {
            yield return BackendClient.Post<AckResponse>("/api/grants/ack", new AckRequest { ids = applied }, (ok, r, e) =>
            {
                if (!ok) Debug.LogWarning($"[Valcoin] ack failed (will retry next tick): {e}");
            });
        }

        // Reconcile the cache DOWN from the backend before saying any number out
        // loud.
        //
        // TryApplyGrant can only add a delta to whatever it already held, and it
        // holds 0 for a player it has never recorded — so on a fresh or drifted
        // cache the running total is fiction. It read 2 for a player with 17,000
        // coins on the ledger, because a 2-coin daily quest was the first grant it
        // had ever written for them. Nothing pulled the real number back, so the
        // gap only ever widened.
        //
        // The backend is the ledger, so after the ack we simply ASK it what the
        // balance is and store that. One GET per player per batch, and only when
        // there were grants to apply. Deliberately after the ack: post-ack is the
        // one moment the answer is unambiguous, whether or not the ledger counts a
        // grant as credited before it has been delivered.
        foreach (var id in touched)
            yield return Reconcile(id);

        // Toast last, so the balance a player is shown is the reconciled one.
        foreach (var g in toToast)
        {
            int bal = CoinManager.GetBalance(g.steam64);
            var player = SteamIdResolver.OnlinePlayerFor(g.steam64);
            if (player != null)
                player.Message(MessageHud.MessageType.TopLeft,
                    $"<color=yellow>+{g.coins} Valcoins!</color>  Balance: {bal}");
            else
                Debug.Log($"[Valcoin] +{g.coins} to {g.steam64} (offline). Balance: {bal}");
        }
    }

    // Pull the authoritative balance for one player and overwrite the cache with
    // it. Best-effort: on failure the cache keeps its accumulated number and the
    // next batch tries again — a missed reconcile is a stale display, never a
    // refused purchase, because nothing gates a spend on this value any more.
    private IEnumerator Reconcile(string steam64)
    {
        if (string.IsNullOrEmpty(steam64)) yield break;

        yield return BackendClient.Get<StateResponse>($"/api/state/{steam64}?top=0", (ok, r, e) =>
        {
            if (!ok || r == null)
            {
                Debug.LogWarning($"[Valcoin] balance reconcile failed for {steam64}: {e ?? "no response"}");
                return;
            }
            int cached = CoinManager.GetBalance(steam64);
            if (cached != r.balance)
                Debug.Log($"[Valcoin] balance reconciled for {steam64}: cache {cached} -> ledger {r.balance}.");
            CoinManager.SetBalance(steam64, r.balance);
        });
    }
}
