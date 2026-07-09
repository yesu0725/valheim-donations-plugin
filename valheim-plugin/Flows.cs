using System;
using UnityEngine;

// Tiny wrappers that talk to the backend on behalf of the slash-command
// handler AND the GUI panel action router. Splitting these out avoids
// duplicating the HTTP plumbing in two places.

public static class DonateFlow
{
    private class ClaimResp
    {
        public string code;
        public string expires_at;
        public string donation_url;
        public int    ttl_minutes;
    }

    // Reply protocol (consumed by DonationPanel.AddLog):
    //   "__DONATE__:<code>|<url>|<ttlMinutes>"  on success
    //   "__DONATE_ERR__:<message>"              on failure
    // The panel renders the code inline with Copy / Open-portal buttons rather
    // than dumping it into a message log.
    public static void Run(string steam64, string senderName, Action<string> reply)
    {
        if (string.IsNullOrEmpty(steam64)) { reply("__DONATE_ERR__:Couldn't resolve your Steam ID."); return; }
        if (!Config.Ready) { reply("__DONATE_ERR__:Donations aren't set up on this server yet."); return; }

        SharedCoroutineRunner.Instance.StartCoroutine(BackendClient.Post<ClaimResp>(
            "/api/claim",
            new { steam64, name = senderName },
            (ok, r, err) =>
            {
                if (!ok || r == null)
                {
                    reply("__DONATE_ERR__:Couldn't reach the donation service. Please try again.");
                    Debug.LogWarning($"[Valcoin] donate action failed: {err}");
                    return;
                }
                reply($"__DONATE__:{r.code}|{r.donation_url}|{r.ttl_minutes}");
            }));
    }
}

public static class GiftFlow
{
    private class TransferResp { public string status; public int balance; public int transferred; }

    public static void Run(string fromSteam64, string fromName, string toName, int amount, Action<string> reply)
    {
        if (string.IsNullOrEmpty(fromSteam64)) { reply("Couldn't resolve your Steam ID."); return; }
        if (string.IsNullOrEmpty(toName))      { reply("Specify a recipient."); return; }
        if (amount <= 0)                       { reply("Amount must be positive."); return; }

        if (!ResolveTargetByName(toName, out var toSteam64))
        { reply($"Player \"{toName}\" not found or no Steam ID."); return; }

        if (toSteam64 == fromSteam64) { reply("You can't gift yourself."); return; }

        int bal = CoinManager.GetBalance(fromSteam64);
        if (bal < amount) { reply($"Not enough Valcoins ({bal} / {amount})."); return; }

        var key = $"gift-{Guid.NewGuid():N}";
        SharedCoroutineRunner.Instance.StartCoroutine(BackendClient.Post<TransferResp>(
            "/api/transfer",
            new {
                from_steam64 = fromSteam64,
                to_steam64   = toSteam64,
                coins        = amount,
                idempotency_key = key,
                from_name    = fromName,
                to_name      = toName,
            },
            (ok, r, err) =>
            {
                if (!ok || r == null) { reply($"Gift failed. ({err ?? "unknown"})"); return; }
                CoinManager.SetBalance(fromSteam64, r.balance);
                reply($"Sent {amount} Valcoins to {toName}. Your balance: {r.balance}");
            }));
    }

    private static bool ResolveTargetByName(string name, out string steam64)
    {
        steam64 = null;
        if (ZNet.instance == null) return false;
        foreach (var p in ZNet.instance.GetConnectedPeers())
        {
            if (p.m_playerName != null && p.m_playerName.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                steam64 = SteamIdResolver.FromPeer(p);
                return !string.IsNullOrEmpty(steam64);
            }
        }
        return false;
    }
}

public static class TopDonorsFetcher
{
    private class TopResp { public Entry[] donors; }
    private class Entry   { public int rank; public string name; public int total_coins; }

    public static void Fetch(Action<string> emit, int limit = 5)
    {
        if (!Config.Ready) { emit("⚠️ Leaderboard unavailable."); return; }
        SharedCoroutineRunner.Instance.StartCoroutine(BackendClient.Get<TopResp>(
            $"/api/leaderboard/top?limit={limit}",
            (ok, r, err) =>
            {
                if (!ok || r?.donors == null) { emit($"Couldn't fetch leaderboard. ({err ?? "unknown"})"); return; }
                if (r.donors.Length == 0)     { emit("No donors yet. Be the first - open the Donate tab!"); return; }

                emit("Top donors:");
                foreach (var e in r.donors)
                    emit($"  {e.rank}. {e.name ?? "Anonymous"} - {e.total_coins} coins");
            }));
    }
}
