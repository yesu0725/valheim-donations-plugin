using System.Collections;
using UnityEngine;

// Client-side bridge from ServerGuide quest completions to Valcoin payouts.
//
// ServerGuide has no currency reward type, so a quest signals completion with
// its stock `set_player_key` reward, setting "VC.Q.<id>" on the character. This
// watcher polls for those keys, reports them to the server over the existing
// silent action RPC, and clears the key.
//
// Clearing matters: it's what re-arms a daily. The quest fires again tomorrow,
// sets the key again, and gets reported again — the backend decides whether
// that second report is worth anything. Nothing here tracks days.
//
// Polling rather than hooking, for the same reason GrantPoller and CatalogSync
// do: there's no event to hook (ServerGuide sets the key internally), and a
// poll self-heals if a tick is ever missed.
public class QuestWatcher : MonoBehaviour
{
    private const float IntervalSeconds = 5f;

    private Coroutine _loop;

    private void Start() => _loop = StartCoroutine(Loop());

    private void OnDestroy()
    {
        if (_loop != null) StopCoroutine(_loop);
    }

    private IEnumerator Loop()
    {
        while (true)
        {
            yield return new WaitForSeconds(IntervalSeconds);

            var player = Player.m_localPlayer;
            if (player == null) continue;
            // The quest list arrives from the server via the catalog sync RPC;
            // until it does there are no keys worth looking for.
            if (QuestCatalog.Order.Count == 0) continue;

            foreach (var quest in QuestCatalog.Order)
            {
                var key = QuestCatalog.KeyFor(quest.Id);
                if (!player.HaveUniqueKey(key)) continue;

                // Clear before reporting so a slow round-trip can't produce a
                // second report on the next tick. ZRoutedRpc is reliable, so
                // the report itself doesn't need a retry path.
                player.RemoveUniqueKey(key);
                RpcLayer.SendAction("quest:" + quest.Id);
                Debug.Log($"[Valcoin] Quest '{quest.Id}' completed; reported to server.");
            }
        }
    }
}
