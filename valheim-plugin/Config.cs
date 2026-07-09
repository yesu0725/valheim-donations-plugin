using System;
using System.IO;
using BepInEx;
using Newtonsoft.Json.Linq;
using UnityEngine;

// Loads and exposes plugin runtime config from BepInEx/config/valcoin_config.json.
// Env vars override the file (handy for hosted dedicated servers like G-Portal).
public static class Config
{
    public static string BackendUrl { get; private set; }
    public static string PluginToken { get; private set; }
    public static float PollIntervalSeconds { get; private set; } = 10f;

    // Phase 5 UI / advertising:
    public static string UiToggleKey { get; private set; } = "F8";
    public static string CodexToggleKey { get; private set; } = "F4";   // Donation Codex
    public static bool   WelcomeEnabled { get; private set; } = true;
    public static string WelcomeMessage { get; private set; }   // null → auto

    public static bool Ready => !string.IsNullOrEmpty(BackendUrl) && !string.IsNullOrEmpty(PluginToken);

    public static void Load()
    {
        BackendUrl  = Environment.GetEnvironmentVariable("VALCOIN_BACKEND_URL");
        PluginToken = Environment.GetEnvironmentVariable("VALCOIN_PLUGIN_TOKEN");

        try
        {
            var path = Path.Combine(Paths.ConfigPath, "valcoin_config.json");
            if (File.Exists(path))
            {
                var json = JObject.Parse(File.ReadAllText(path));
                BackendUrl  = string.IsNullOrEmpty(BackendUrl)  ? (string)json["backend_url"]  : BackendUrl;
                PluginToken = string.IsNullOrEmpty(PluginToken) ? (string)json["plugin_token"] : PluginToken;
                if (json["poll_interval_seconds"] != null)
                    PollIntervalSeconds = (float)json["poll_interval_seconds"];
                if (json["ui_toggle_key"] != null)
                    UiToggleKey = (string)json["ui_toggle_key"];
                if (json["codex_toggle_key"] != null)
                    CodexToggleKey = (string)json["codex_toggle_key"];
                if (json["welcome_message_enabled"] != null)
                    WelcomeEnabled = (bool)json["welcome_message_enabled"];
                if (json["welcome_message"] != null)
                    WelcomeMessage = (string)json["welcome_message"];
            }
            else
            {
                File.WriteAllText(path,
@"{
  ""backend_url"":  ""https://your-app.fly.dev"",
  ""plugin_token"": ""paste-the-PLUGIN_TOKEN-from-your-fly-secrets"",
  ""poll_interval_seconds"": 10,

  ""ui_toggle_key"": ""F8"",
  ""codex_toggle_key"": ""F4"",
  ""welcome_message_enabled"": true,
  ""welcome_message"": null
}
");
                Debug.LogWarning($"[Valcoin] Created template config at {path}. Fill in backend_url + plugin_token.");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Valcoin] Failed to load config: {ex.Message}");
        }

        if (!Ready)
            Debug.LogWarning("[Valcoin] Backend not configured; donation actions and grant polling are disabled.");
    }
}
