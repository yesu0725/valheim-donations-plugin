using System.Collections;
using UnityEngine;

// Broadcasts the server's parsed shop catalog to connected clients.
//
// valcoin_shop.yaml only exists on whichever machine loaded it (the dedicated
// server). A vanilla/remote client's own Catalog.Load() finds no such file,
// so without this it would see an empty shop. Runs on a fixed interval
// (rather than hooking player-connect) so it's resilient to any connection
// timing and self-heals if a broadcast is ever missed — same "poll, don't
// hook" philosophy as GrantPoller.
public class CatalogSync : MonoBehaviour
{
    private const float IntervalSeconds = 30f;
    private Coroutine _loop;

    private void Start() => _loop = StartCoroutine(Loop());

    private void OnDestroy()
    {
        if (_loop != null) StopCoroutine(_loop);
    }

    private IEnumerator Loop()
    {
        while (ZNet.instance == null || !ZNet.instance.IsServer())
            yield return new WaitForSeconds(2f);

        while (true)
        {
            if (ZRoutedRpc.instance != null)
                RpcLayer.BroadcastCatalog(Catalog.Serialize());
            yield return new WaitForSeconds(IntervalSeconds);
        }
    }
}
