using System;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

// Centralised user-id lookup.
//
// The plugin treats user ids as opaque strings — what matters is that the same
// player always resolves to the same id. We accept two formats:
//   * Steam:   17-digit Steam64        (e.g. "76561198012345678")
//   * PlayFab: "PlayFab_" + entity id  (e.g. "PlayFab_8A47B6C2D1E0F9A0")
//
// Player.GetPlayerID() is Valheim's internal player id, NOT either of these,
// so don't use it as a key. The real id lives on the peer's socket hostname
// or in the userInfo of newer chat RPCs.
public static class SteamIdResolver
{
    private static readonly Regex Steam64Re = new Regex(@"^7656119\d{10}$", RegexOptions.Compiled);
    private static readonly Regex PlayFabIdRe = new Regex(@"^[A-Za-z0-9]{8,32}$", RegexOptions.Compiled);

    private const string PlayFabPrefix = "PlayFab_";

    /// Returns either a Steam64 ("7656...") or a PlayFab id ("PlayFab_..."), or null.
    public static string FromPeer(ZNetPeer peer)
    {
        if (peer == null) return null;
        try
        {
            var socket = peer.m_rpc?.GetSocket();
            if (socket == null) return null;

            var host = socket.GetHostName();
            if (!string.IsNullOrEmpty(host) && Steam64Re.IsMatch(host))
                return host;

            // PlayFab/crossplay players: socket type name typically contains "PlayFab"
            // and the hostname is the PlayFab entity id (alphanumeric).
            var socketType = socket.GetType().Name ?? "";
            if (socketType.IndexOf("PlayFab", StringComparison.OrdinalIgnoreCase) >= 0
                && !string.IsNullOrEmpty(host) && PlayFabIdRe.IsMatch(host))
            {
                return PlayFabPrefix + host;
            }
        }
        catch { }
        return null;
    }

    /// Strip the prefix Valheim adds in newer chat RPCs ("Steam_..." or "Pla_...").
    public static string FromNetworkUserId(string nuid)
    {
        if (string.IsNullOrEmpty(nuid)) return null;

        if (nuid.StartsWith("Steam_"))
        {
            var bare = nuid.Substring("Steam_".Length);
            return Steam64Re.IsMatch(bare) ? bare : null;
        }
        // PlayFab IDs are prefixed "Pla_<id>" in some Valheim builds.
        if (nuid.StartsWith("Pla_") || nuid.StartsWith("PlayFab_"))
        {
            var bare = nuid.StartsWith("Pla_") ? nuid.Substring("Pla_".Length)
                                                : nuid.Substring("PlayFab_".Length);
            return PlayFabIdRe.IsMatch(bare) ? PlayFabPrefix + bare : null;
        }
        // Bare Steam64 with no prefix — accept it.
        return Steam64Re.IsMatch(nuid) ? nuid : null;
    }

    /// Best-effort resolve from any peer ID the server may hand us.
    public static string FromPeerId(long peerId)
    {
        if (peerId == 0 || ZNet.instance == null) return null;
        return FromPeer(ZNet.instance.GetPeer(peerId));
    }

    /// Find the connected ZNetPeer for a given Steam64/PlayFab id.
    public static ZNetPeer PeerFor(string steam64)
    {
        if (string.IsNullOrEmpty(steam64) || ZNet.instance == null) return null;
        return ZNet.instance.GetConnectedPeers()
            .FirstOrDefault(p => FromPeer(p) == steam64);
    }

    /// Resolve the player character ZDO for this Steam64. Lets server-side code
    /// read/write position even when the Player MonoBehaviour lives on the client.
    public static ZDO ZdoFor(string steam64)
    {
        var peer = PeerFor(steam64);
        if (peer == null || ZDOMan.instance == null) return null;
        return ZDOMan.instance.GetZDO(peer.m_characterID);
    }

    /// Find the online Player whose connection matches this Steam64.
    public static Player OnlinePlayerFor(string steam64)
    {
        if (string.IsNullOrEmpty(steam64) || ZNet.instance == null) return null;
        var match = ZNet.instance.GetConnectedPeers()
            .FirstOrDefault(p => FromPeer(p) == steam64);
        if (match == null) return null;

        var name = match.m_playerName;
        return Player.GetAllPlayers()
            .FirstOrDefault(p => p != null && p.GetPlayerName().Equals(name, StringComparison.OrdinalIgnoreCase));
    }
}
