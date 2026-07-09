using UnityEngine;

// Backend HTTP calls run as coroutines, but several server-side handlers
// (ShopHandler, Flows, UiActionRouter) are static classes without a
// MonoBehaviour of their own. This holder gives them a place to spawn
// coroutines from.
public class SharedCoroutineRunner : MonoBehaviour
{
    private static SharedCoroutineRunner _instance;
    public static SharedCoroutineRunner Instance
    {
        get
        {
            if (_instance == null)
            {
                var go = new GameObject("ValcoinCoroutineRunner");
                Object.DontDestroyOnLoad(go);
                _instance = go.AddComponent<SharedCoroutineRunner>();
            }
            return _instance;
        }
    }
}
