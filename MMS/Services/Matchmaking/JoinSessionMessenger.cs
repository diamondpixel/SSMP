using System.Net.WebSockets;
using MMS.Models.Matchmaking;
using MMS.Services.Lobby;
using MMS.Services.Network;
using _Lobby = MMS.Models.Lobby.Lobby;

namespace MMS.Services.Matchmaking;

/// <summary>
/// Sends matchmaking rendezvous messages over client and host WebSockets.
/// </summary>
/// <remarks>
/// Each send helper first verifies that the target socket is still open.
/// Methods returning <see cref="bool"/> let the coordinator distinguish "target missing"
/// from transport exceptions raised during the actual send.
/// </remarks>
public sealed class JoinSessionMessenger(LobbyService lobbyService) {
    /// <summary>
    /// Tells the joining client to begin its UDP mapping phase by sending its
    /// discovery token to the STUN/mapping endpoint.
    /// </summary>
    /// <param name="session">The active join session.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    public static Task SendBeginClientMappingAsync(JoinSession session, CancellationToken cancellationToken) =>
        SendToJoinClientAsync(
            session,
            new {
                action = "begin_client_mapping",
                joinId = session.JoinId,
                clientDiscoveryToken = session.ClientDiscoveryToken,
                serverTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            },
            cancellationToken
        );

    /// <summary>
    /// Asks the host to refresh its NAT mapping by re-sending its UDP discovery packet.
    /// </summary>
    /// <param name="joinId">The join session identifier, forwarded to the host for correlation.</param>
    /// <param name="lobbyConnectionData">Connection data used to locate the lobby and its host socket.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>
    /// <see langword="true"/> if the message was dispatched to an open host socket;
    /// <see langword="false"/> if the host is unavailable or missing a discovery token.
    /// </returns>
    public async Task<bool> SendHostRefreshRequestAsync(
        string joinId,
        string lobbyConnectionData,
        CancellationToken cancellationToken
    ) {
        var lobby = lobbyService.GetLobby(lobbyConnectionData);
        if (lobby?.HostWebSocket is not { State: WebSocketState.Open } hostWs ||
            string.IsNullOrEmpty(lobby.HostDiscoveryToken)) {
            return false;
        }

        await WebSocketMessenger.SendAsync(
            hostWs,
            new {
                action = "refresh_host_mapping",
                joinId,
                hostDiscoveryToken = lobby.HostDiscoveryToken,
                serverTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            },
            cancellationToken
        );
        return true;
    }

    /// <summary>
    /// Notifies the joining client that its external UDP port has been observed by the server.
    /// </summary>
    /// <param name="session">The active join session.</param>
    /// <param name="port">The client's discovered external port.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    public static Task SendClientMappingReceivedAsync(
        JoinSession session,
        int port,
        CancellationToken cancellationToken
    ) =>
        SendToJoinClientAsync(
            session,
            new {
                action = "client_mapping_received",
                clientPort = port,
                serverTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            },
            cancellationToken
        );

    /// <summary>
    /// Notifies the host that its external UDP port has been observed by the server.
    /// </summary>
    /// <param name="lobby">The lobby whose host socket will receive the message.</param>
    /// <param name="port">The host's discovered external port.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    public static Task SendHostMappingReceivedAsync(_Lobby lobby, int port, CancellationToken cancellationToken) =>
        SendToHostAsync(
            lobby,
            new {
                action = "host_mapping_received",
                hostPort = port,
                serverTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            },
            cancellationToken
        );

    /// <summary>
    /// Sends a synchronized NAT punch instruction to the joining client.
    /// </summary>
    /// <param name="session">The active join session.</param>
    /// <param name="hostPort">The host's external UDP port to punch towards.</param>
    /// <param name="hostIp">The host's external IP address.</param>
    /// <param name="startTimeMs">
    /// Coordinated UTC timestamp (Unix ms) at which both sides should begin punching.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns><see langword="true"/> if the client socket was open and the payload was queued for send.</returns>
    public static async Task<bool> SendStartPunchToClientAsync(
        JoinSession session,
        int hostPort,
        string hostIp,
        long startTimeMs,
        CancellationToken cancellationToken
    ) {
        if (session.ClientWebSocket is not { State: WebSocketState.Open } ws)
            return false;

        await WebSocketMessenger.SendAsync(
            ws,
            new {
                action = "start_punch",
                joinId = session.JoinId,
                hostIp,
                hostPort,
                startTimeMs
            },
            cancellationToken
        );
        return true;
    }

    /// <summary>
    /// Sends a synchronized NAT punch instruction to the lobby host.
    /// </summary>
    /// <param name="lobby">The lobby whose host socket will receive the message.</param>
    /// <param name="joinId">The join session identifier for host-side correlation.</param>
    /// <param name="clientIp">The joining client's external IP address.</param>
    /// <param name="clientPort">The joining client's external UDP port.</param>
    /// <param name="hostPort">The host's own external UDP port (echoed back for confirmation).</param>
    /// <param name="startTimeMs">
    /// Coordinated UTC timestamp (Unix ms) at which both sides should begin punching.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns><see langword="true"/> if the host socket was open and the payload was queued for send.</returns>
    public static async Task<bool> SendStartPunchToHostAsync(
        _Lobby lobby,
        string joinId,
        string clientIp,
        int clientPort,
        int hostPort,
        long startTimeMs,
        CancellationToken cancellationToken
    ) {
        if (lobby.HostWebSocket is not { State: WebSocketState.Open } hostWs)
            return false;

        await WebSocketMessenger.SendAsync(
            hostWs,
            new {
                action = "start_punch",
                joinId,
                clientIp,
                clientPort,
                hostPort,
                startTimeMs
            },
            cancellationToken
        );
        return true;
    }

    /// <summary>
    /// Notifies the joining client that the join attempt has failed.
    /// </summary>
    /// <param name="session">The active join session.</param>
    /// <param name="reason">A short machine-readable failure reason (e.g. <c>"host_unreachable"</c>).</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    public static Task SendJoinFailedToClientAsync(
        JoinSession session,
        string reason,
        CancellationToken cancellationToken
    ) =>
        SendToJoinClientAsync(
            session,
            new { action = "join_failed", joinId = session.JoinId, reason },
            cancellationToken
        );

    /// <summary>
    /// Notifies the lobby host that a join attempt has failed.
    /// </summary>
    /// <param name="lobbyConnectionData">Connection data used to locate the lobby and its host socket.</param>
    /// <param name="joinId">The join session identifier for host-side correlation.</param>
    /// <param name="reason">A short machine-readable failure reason (e.g. <c>"host_unreachable"</c>).</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    public async Task SendJoinFailedToHostAsync(
        string lobbyConnectionData,
        string joinId,
        string reason,
        CancellationToken cancellationToken
    ) {
        var lobby = lobbyService.GetLobby(lobbyConnectionData);
        if (lobby?.HostWebSocket is not { State: WebSocketState.Open } hostWs)
            return;

        await WebSocketMessenger.SendAsync(
            hostWs,
            new { action = "join_failed", joinId, reason },
            cancellationToken
        );
    }

    /// <summary>
    /// Sends <paramref name="payload"/> to the session's client WebSocket, if open.
    /// </summary>
    private static Task SendToJoinClientAsync(
        JoinSession session,
        object payload,
        CancellationToken cancellationToken
    ) {
        return session.ClientWebSocket is not { State: WebSocketState.Open } ws
            ? Task.CompletedTask
            : WebSocketMessenger.SendAsync(ws, payload, cancellationToken);
    }

    /// <summary>
    /// Sends <paramref name="payload"/> to the lobby's host WebSocket, if open.
    /// </summary>
    private static Task SendToHostAsync(
        _Lobby lobby,
        object payload,
        CancellationToken cancellationToken
    ) {
        return lobby.HostWebSocket is not { State: WebSocketState.Open } ws
            ? Task.CompletedTask
            : WebSocketMessenger.SendAsync(ws, payload, cancellationToken);
    }
}
