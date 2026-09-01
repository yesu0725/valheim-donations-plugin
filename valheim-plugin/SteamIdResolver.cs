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

    // ─── the host is not one of its own peers ──────────────────────────────
    //
    // Everything above resolves a player by walking ZNet's PEER list, which is
    // the list of *remote* connections. On a listen server (someone hosting from
    // their own game, rather than a dedicated server) the host is not in it —
    // ZNet.IsConnected special-cases `uid == GetUID()` before it scans m_peers,
    // for exactly that reason. So every lookup here returns null for the host,
    // and server-side code that asks "who is this / where are they" gets nothing
    // back for the one player sitting at the keyboard.
    //
    // These two helpers are the seam. A dedicated server never satisfies
    // IsListenServer(), so nothing below can change its behaviour.

    /// True when this process is a server that also has a local player — i.e. a
    /// host, not a dedicated server and not a plain client.
    public static bool IsListenServer() =>
        ZNet.instance != null && ZNet.instance.IsServer() && !ZNet.instance.IsDedicated();

    /// This id belongs to the local host themselves (so peer-list lookups will
    /// never find it, and the local Player is the answer).
    public static bool IsLocalHost(string id)
    {
        if (string.IsNullOrEmpty(id) || !IsListenServer()) return false;
        var mine = LocalIdentity.Steam64();
        return !string.IsNullOrEmpty(mine) && mine == id;
    }

    /// Resolve the player character ZDO for this Steam64. Lets server-side code
    /// read/write position even when the Player MonoBehaviour lives on the client.
    public static ZDO ZdoFor(string steam64)
    {
        var peer = PeerFor(steam64);
        if (peer != null)
            return ZDOMan.instance != null ? ZDOMan.instance.GetZDO(peer.m_characterID) : null;

        // The host: no peer entry, but the character is right here. Without this
        // a host's grant_item purchase is refused by Buy's pre-check ("couldn't
        // find your character to deliver items") — which at least refuses before
        // debiting, but refuses every time.
        if (IsLocalHost(steam64))
            return Player.m_localPlayer != null
                ? Player.m_localPlayer.GetComponent<ZNetView>()?.GetZDO()
                : null;

        return null;
    }

    /// Find the online Player whose connection matches this Steam64.
    public static Player OnlinePlayerFor(string steam64)
    {
        if (string.IsNullOrEmpty(steam64) || ZNet.instance == null) return null;

        var match = ZNet.instance.GetConnectedPeers()
            .FirstOrDefault(p => FromPeer(p) == steam64);
        if (match != null)
        {
            var name = match.m_playerName;
            return Player.GetAllPlayers()
                .FirstOrDefault(p => p != null && p.GetPlayerName().Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        // The host is their own local player. This is what puts the
        // "+N Valcoins!" toast in front of a host on grant delivery.
        return IsLocalHost(steam64) ? Player.m_localPlayer : null;
    }
}
