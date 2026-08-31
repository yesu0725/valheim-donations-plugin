using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;

// Tiny async HTTPS client used by the plugin to talk to the donations backend.
// Everything is fire-and-forget coroutines so the main thread never blocks.
public static class BackendClient
{
    public delegate void Callback<T>(bool ok, T result, string error);

    public static IEnumerator Get<T>(string path, Callback<T> cb)
    {
        return Send("GET", path, null, cb);
    }

    public static IEnumerator Post<T>(string path, object body, Callback<T> cb)
    {
        return Send("POST", path, body, cb);
    }

    // Retries a POST until it succeeds, the backend gives a definite answer, or
    // the attempts run out.
    //
    // ONLY FOR IDEMPOTENT CALLS. Every caller must send a stable
    // idempotency_key, because the whole point is that a request whose REPLY was
    // lost gets asked again: the backend either commits it once or answers
    // "duplicate", and either way we finally learn what happened to the player's
    // coins. Without this, a spend that committed on the server and lost its
    // response on the way home looked exactly like a spend that never happened -
    // and the player was told their purchase failed while their balance dropped.
    public static IEnumerator PostWithRetry<T>(string path, object body, int attempts,
                                               float retryDelaySeconds, Callback<T> cb)
    {
        string lastErr = null;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            if (attempt > 1) yield return new WaitForSeconds(retryDelaySeconds);

            bool ok = false; T result = default; string err = null;
            yield return Post<T>(path, body, (o, r, e) => { ok = o; result = r; err = e; });

            if (ok) { cb?.Invoke(true, result, null); yield break; }

            lastErr = err;
            if (IsDefiniteRefusal(err)) break;
            if (attempt < attempts)
                Debug.LogWarning($"[Valcoin] {path} attempt {attempt}/{attempts} failed ({err}); retrying.");
        }
        cb?.Invoke(false, default, lastErr);
    }

    // True when the backend answered and said no. Anything else - a timeout, a
    // dropped connection, a 5xx, a reply we couldn't parse - leaves the outcome
    // unknown, which is exactly the case a retry is for.
    //
    // A 200 whose body failed to parse is deliberately treated as unknown too:
    // the write DID land, and only a retry (answered "duplicate") reveals that.
    public static bool IsDefiniteRefusal(string err)
    {
        if (string.IsNullOrEmpty(err)) return false;
        if (err.StartsWith("backend not configured", StringComparison.Ordinal)) return true;

        int space = err.IndexOf(' ');
        if (space > 0 && int.TryParse(err.Substring(0, space), out int code))
            return code >= 400 && code < 500 && code != 408 && code != 425;
        return false;
    }

    private static IEnumerator Send<T>(string method, string path, object body, Callback<T> cb)
    {
        if (!Config.Ready)
        {
            cb?.Invoke(false, default, "backend not configured (valcoin_config.json missing backend_url/plugin_token)");
            yield break;
        }

        string url = Config.BackendUrl.TrimEnd('/') + path;
        using var req = new UnityWebRequest(url, method);
        req.timeout = 15;
        req.SetRequestHeader("Authorization", "Bearer " + Config.PluginToken);
        req.SetRequestHeader("Accept", "application/json");
        req.downloadHandler = new DownloadHandlerBuffer();

        if (body != null)
        {
            string json = JsonConvert.SerializeObject(body);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            req.uploadHandler = new UploadHandlerRaw(bytes);
            req.SetRequestHeader("Content-Type", "application/json");
        }

        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            cb?.Invoke(false, default, $"{(int)req.responseCode} {req.error}: {req.downloadHandler?.text}");
            yield break;
        }

        T parsed = default;
        try
        {
            string text = req.downloadHandler.text;
            parsed = string.IsNullOrEmpty(text) ? default : JsonConvert.DeserializeObject<T>(text);
        }
        catch (Exception ex)
        {
            cb?.Invoke(false, default, "json parse failed: " + ex.Message);
            yield break;
        }

        cb?.Invoke(true, parsed, null);
    }
}
