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
        foreach (var g in pending.grants)
        {
            try
            {
                bool firstApply = CoinManager.TryApplyGrant(g.id, g.steam64, g.coins);
                int bal = CoinManager.GetBalance(g.steam64);

                if (firstApply)
                {
                    var player = SteamIdResolver.OnlinePlayerFor(g.steam64);
                    if (player != null)
                    {
                        player.Message(MessageHud.MessageType.TopLeft,
                            $"<color=yellow>+{g.coins} Valcoins!</color>  Balance: {bal}");
                    }
                    else
                    {
                        Debug.Log($"[Valcoin] +{g.coins} to {g.steam64} (offline). Balance: {bal}");
                    }
                }
                else
                {
                    Debug.Log($"[Valcoin] grant {g.id} replay (already applied locally); will re-ack.");
                }

                applied.Add(g.id);
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
    }
}
