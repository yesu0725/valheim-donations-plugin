using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Client-side bridge from ServerGuide quest completions to Valcoin payouts.
//
// ServerGuide has no currency reward type, so a quest signals completion with
// its stock `set_player_key` reward, setting "VC.Q.<id>" on the character. This
// watcher polls for those keys, reports them to the server over the existing
// silent action RPC, and clears the key ONCE THE SERVER ACKNOWLEDGES.
//
// Clearing on ack, not on send, is the important part. An earlier version
// cleared immediately on the reasoning that ZRoutedRpc is reliable — but
// transport reliability only says the message arrived, not that the server
// understood it. A server running an older build (no quest handler at all), a
// backend outage, or a quest with no configured price would each silently
// destroy the completion: key gone, no coins, and a `once: true` quest can
// never fire again to make up for it. That happened. Now an unacknowledged
// report is simply retried, so the payout is postponed rather than lost.
//
// Clearing is also what re-arms a daily: the quest fires again tomorrow, sets
// the key again, and gets reported again. Nothing here tracks days — the
// backend owns that.
//
// Polling rather than hooking, for the same reason GrantPoller and CatalogSync
// do: there's no event to hook (ServerGuide sets the key internally), and a
// poll self-heals if a tick is ever missed.
public class QuestWatcher : MonoBehaviour
{
    private const float IntervalSeconds = 5f;
    // How long to wait for an ack before reporting the same quest again. Long
    // enough that a server which will never ack (old build) produces a slow
    // trickle rather than a flood, short enough that a transient backend blip
    // resolves within a minute.
    private const float RetrySeconds = 60f;

    // questId -> Time.realtimeSinceStartup when we last reported it. Session
    // state only; the key on the character is the durable record, so a relog
    // just re-reports.
    private static readonly Dictionary<string, float> _reportedAt = new Dictionary<string, float>();

    private Coroutine _loop;

    private void Start() => _loop = StartCoroutine(Loop());

    private void OnDestroy()
    {
        if (_loop != null) StopCoroutine(_loop);
        _reportedAt.Clear();
    }

    /// Server confirmed it reached a definitive answer for this quest, so the
    /// key has done its job and can be cleared. Called from RpcLayer.
    public static void OnAck(string questId)
    {
        if (string.IsNullOrEmpty(questId)) return;
        _reportedAt.Remove(questId);

        var player = Player.m_localPlayer;
        if (player == null) return;

        var key = QuestCatalog.KeyFor(questId);
        if (!player.HaveUniqueKey(key)) return;

        player.RemoveUniqueKey(key);
        Debug.Log($"[Valcoin] Quest '{questId}' acknowledged by server; key cleared.");
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

            var now = Time.realtimeSinceStartup;

            foreach (var quest in QuestCatalog.Order)
            {
                if (!player.HaveUniqueKey(QuestCatalog.KeyFor(quest.Id))) continue;

                // Awaiting an ack for a recent report — don't pile on.
                if (_reportedAt.TryGetValue(quest.Id, out var last)
                    && now - last < RetrySeconds) continue;

                bool retry = _reportedAt.ContainsKey(quest.Id);
                _reportedAt[quest.Id] = now;
                RpcLayer.SendAction("quest:" + quest.Id);

                Debug.Log(retry
                    ? $"[Valcoin] Quest '{quest.Id}' still unacknowledged; re-reporting."
                    : $"[Valcoin] Quest '{quest.Id}' completed; reported to server.");
            }
        }
    }
}
