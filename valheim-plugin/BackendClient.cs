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
