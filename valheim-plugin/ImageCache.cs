using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using UnityEngine;
using UnityEngine.Networking;

// Lazy async loader/cache for shop preview images. Each source string is
// fetched at most once; callers just read Get(source) every OnGUI frame and
// receive null until the texture is ready (or forever, if it failed).
//
// A source may be:
//   https://.../thumb.png   — downloaded over HTTP(S)
//   file:///C:/.../x.png    — explicit file URI
//   shop_images/x.png       — resolved relative to BepInEx/config
//
// Preview images ride along with the catalog: the server-side YAML sets the
// field and CatalogSync serializes it to remote clients, so a URL configured
// once is visible to everyone. (Config-relative *paths* only resolve on
// machines that actually have the file — prefer URLs for a dedicated server.)
public static class ImageCache
{
    private enum Status { Loading, Ready, Failed }

    private class Entry
    {
        public Status Status;
        public Texture2D Texture;
    }

    private static readonly Dictionary<string, Entry> _cache = new Dictionary<string, Entry>();

    // The cached texture, or null while it's loading / if it failed. The first
    // call for a given source kicks off the (async) load.
    public static Texture2D Get(string source)
    {
        if (string.IsNullOrEmpty(source)) return null;
        if (_cache.TryGetValue(source, out var e))
            return e.Status == Status.Ready ? e.Texture : null;

        var entry = new Entry { Status = Status.Loading };
        _cache[source] = entry;
        SharedCoroutineRunner.Instance.StartCoroutine(Load(source, entry));
        return null;
    }

    private static IEnumerator Load(string source, Entry entry)
    {
        string url = ToUrl(source);
        if (url == null) { entry.Status = Status.Failed; yield break; }

        using var req = UnityWebRequestTexture.GetTexture(url);
        req.timeout = 15;
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogWarning($"[Valcoin] Shop image load failed ({source}): {req.error}");
            entry.Status = Status.Failed;
            yield break;
        }

        var tex = DownloadHandlerTexture.GetContent(req);
        if (tex == null) { entry.Status = Status.Failed; yield break; }
        tex.wrapMode = TextureWrapMode.Clamp;
        entry.Texture = tex;
        entry.Status = Status.Ready;
    }

    // Map a catalog source string to a loadable URL. http(s)/file URIs pass
    // through; anything else is treated as a path relative to BepInEx/config
    // (an absolute local path also works) and converted to a file:// URI.
    private static string ToUrl(string source)
    {
        try
        {
            if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || source.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                return source;

            string path = Path.IsPathRooted(source) ? source : Path.Combine(Paths.ConfigPath, source);
            if (!File.Exists(path))
            {
                Debug.LogWarning($"[Valcoin] Shop image not found: {path}");
                return null;
            }
            return new Uri(path).AbsoluteUri;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Valcoin] Shop image path error ({source}): {ex.Message}");
            return null;
        }
    }
}
