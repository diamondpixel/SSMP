using System.Collections.Concurrent;
using System.Net.WebSockets;
using MMS.Models.Matchmaking;
using MMS.Services.Lobby;
using MMS.Services.Network;
using MMS.Services.Utility;
using _Lobby = MMS.Models.Lobby.Lobby;

namespace MMS.Services.Matchmaking;

/// <summary>
/// Manages the lifecycle of matchmaking join sessions and NAT hole-punch coordination.
/// </summary>
/// <remarks>
/// A join session represents a single client attempt to connect to a lobby host.
/// The typical flow is:
/// <list type="number">
///   <item>
///     Client calls <c>POST /lobby/{id}/join</c> ->
///     <see cref="CreateJoinSession"/> allocates a session and a client discovery token.
///   </item>
///   <item>
///     Client opens a WebSocket ->
///     <see cref="AttachJoinWebSocket"/> stores it on the session so the service can push events.
///   </item>
///   <item>
///     MMS receives the client's UDP discovery packet ->
///     <see cref="SetDiscoveredPortAsync"/> records the client's external port and sends a
///     <c>refresh_host_mapping</c> request to the host.
///   </item>
///   <item>
///     MMS receives the host's UDP discovery packet ->
///     <see cref="SetDiscoveredPortAsync"/> records the host's external port and calls
///     <see cref="TryStartJoinSessionAsync"/>, which sends synchronized <c>start_punch</c>
///     messages to both sides.
///   </item>
/// </list>
/// </remarks>
public class JoinSessionService(LobbyService lobbyService, ILogger<JoinSessionService> logger)
{
    private readonly ConcurrentDictionary<string, JoinSession> _joinSessions = new();
    private readonly ConcurrentDictionary<string, DiscoveryTokenMetadata> _discoveryMetadata = new();

    #region Join session management

    /// <summary>
    /// Creates a join session for a client connecting to <paramref name="lobby"/>.
    /// </summary>
    /// <remarks>
    /// Steam lobbies use the Steam relay and do not require NAT hole-punching,
    /// so this method returns <see langword="null"/> for them.
    /// The host's discovery token is registered in <see cref="_discoveryMetadata"/> on the
    /// first join so that subsequent UDP packets from the host can be matched back to its lobby.
    /// </remarks>
    /// <param name="lobby">The lobby the client is joining.</param>
    /// <param name="clientIp">The client's IP address as observed by the server.</param>
    /// <returns>
    /// A new <see cref="JoinSession"/>, or <see langword="null"/> for Steam lobbies.
    /// </returns>
    public JoinSession? CreateJoinSession(_Lobby lobby, string clientIp)
    {
        if (lobby.LobbyType.Equals("steam", StringComparison.OrdinalIgnoreCase))
            return null;

        var joinId = TokenGenerator.GenerateToken(32);
        var clientDiscoveryToken = TokenGenerator.GenerateToken(32);

        var session = new JoinSession
        {
            JoinId = joinId,
            LobbyConnectionData = lobby.ConnectionData,
            ClientIp = clientIp,
            ClientDiscoveryToken = clientDiscoveryToken
        };

        _joinSessions[joinId] = session;
        _discoveryMetadata[clientDiscoveryToken] = new DiscoveryTokenMetadata { JoinId = joinId };

        // Register the host discovery token on first join so incoming host UDP packets
        // can be matched to the correct lobby.
        if (lobby.HostDiscoveryToken != null && !_discoveryMetadata.ContainsKey(lobby.HostDiscoveryToken))
        {
            _discoveryMetadata[lobby.HostDiscoveryToken] = new DiscoveryTokenMetadata
            {
                HostConnectionData = lobby.ConnectionData
            };
        }

        return session;
    }

    /// <summary>
    /// Returns a non-expired join session, or <see langword="null"/> if absent or expired.
    /// Expired sessions are removed lazily on access.
    /// </summary>
    /// <param name="joinId">The join session identifier.</param>
    public JoinSession? GetJoinSession(string joinId)
    {
        if (!_joinSessions.TryGetValue(joinId, out var session)) return null;

        if (session.ExpiresAtUtc >= DateTime.UtcNow) return session;

        CleanupJoinSession(joinId);
        return null;
    }

    /// <summary>
    /// Attaches the client's WebSocket to an existing join session so that the service
    /// can push <c>begin_client_mapping</c> and <c>start_punch</c> events to it.
    /// </summary>
    /// <param name="joinId">The join session identifier.</param>
    /// <param name="webSocket">The open WebSocket accepted from the client.</param>
    /// <returns>
    /// <see langword="true"/> if the session was found and the socket attached;
    /// <see langword="false"/> if the session has expired or does not exist.
    /// </returns>
    public bool AttachJoinWebSocket(string joinId, WebSocket webSocket)
    {
        var session = GetJoinSession(joinId);
        if (session == null) return false;

        session.ClientWebSocket = webSocket;
        return true;
    }

    #endregion

    #region Discovery port tracking

    /// <summary>
    /// Records the external port observed from a UDP discovery packet and advances the
    /// hole-punch state machine.
    /// </summary>
    /// <remarks>
    /// Token routing:
    /// <list type="bullet">
    ///   <item>
    ///     <b>Host token</b> - updates <see cref="_Lobby.ExternalPort"/> and attempts to start
    ///     all pending join sessions for that lobby.
    ///   </item>
    ///   <item>
    ///     <b>Client token</b> - updates <see cref="JoinSession.ClientExternalPort"/>, sends
    ///     a <c>refresh_host_mapping</c> request to the host, then attempts to start the session.
    ///   </item>
    /// </list>
    /// </remarks>
    /// <param name="token">The discovery token extracted from the incoming UDP packet.</param>
    /// <param name="port">The external port observed for the sender.</param>
    /// <param name="cancellationToken">Token used to cancel any downstream async operations.</param>
    public async Task SetDiscoveredPortAsync(string token, int port, CancellationToken cancellationToken = default)
    {
        if (!_discoveryMetadata.TryGetValue(token, out var metadata)) return;

        metadata.DiscoveredPort = port;

        if (metadata.HostConnectionData != null)
        {
            await HandleHostPortDiscoveredAsync(metadata.HostConnectionData, port, cancellationToken);
            return;
        }

        if (metadata.JoinId != null)
            await HandleClientPortDiscoveredAsync(metadata.JoinId, port, cancellationToken);
    }

    /// <summary>
    /// Returns the external port most recently observed for <paramref name="token"/>,
    /// or <see langword="null"/> if no UDP packet has been received yet.
    /// </summary>
    /// <param name="token">The discovery token to query.</param>
    public int? GetDiscoveredPort(string token) =>
        _discoveryMetadata.TryGetValue(token, out var metadata) ? metadata.DiscoveredPort : null;

    /// <summary>
    /// Removes a discovery token and its associated metadata.
    /// </summary>
    /// <param name="token">The discovery token to remove.</param>
    private void RemoveDiscoveryToken(string token) =>
        _discoveryMetadata.TryRemove(token, out _);

    #endregion

    #region WebSocket messaging

    /// <summary>
    /// Sends a <c>begin_client_mapping</c> message to the client's WebSocket,
    /// instructing it to begin sending UDP discovery packets.
    /// </summary>
    /// <param name="joinId">The join session identifier.</param>
    /// <param name="cancellationToken">Token used to cancel the send operation.</param>
    public Task SendBeginClientMappingAsync(string joinId, CancellationToken cancellationToken)
    {
        var session = GetJoinSession(joinId);
        return SendToJoinClientAsync(
            joinId,
            new
            {
                action = "begin_client_mapping",
                joinId,
                clientDiscoveryToken = session?.ClientDiscoveryToken,
                serverTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            },
            cancellationToken
        );
    }

    /// <summary>
    /// Sends a <c>refresh_host_mapping</c> message to the host's persistent WebSocket,
    /// asking the host to send a fresh UDP discovery packet so MMS can re-learn its
    /// current external port.
    /// </summary>
    /// <param name="joinId">The join session identifier.</param>
    /// <param name="cancellationToken">Token used to cancel the send operation.</param>
    /// <returns>
    /// <see langword="true"/> if the message was sent; <see langword="false"/> if the host
    /// WebSocket is unavailable or the session no longer exists.
    /// </returns>
    public async Task<bool> SendHostRefreshRequestAsync(string joinId, CancellationToken cancellationToken)
    {
        var session = GetJoinSession(joinId);
        if (session == null) return false;

        var lobby = lobbyService.GetLobby(session.LobbyConnectionData);
        if (lobby?.HostWebSocket is not { State: WebSocketState.Open } ws ||
            string.IsNullOrEmpty(lobby.HostDiscoveryToken))
        {
            return false;
        }

        await WebSocketMessenger.SendAsync(
            ws,
            new
            {
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
    /// Marks a join session as failed, sends a <c>join_failed</c> message to both the client
    /// and the host, then removes the session.
    /// </summary>
    /// <param name="joinId">The join session identifier.</param>
    /// <param name="reason">A short machine-readable reason string included in the failure message.</param>
    /// <param name="cancellationToken">Token used to cancel send operations.</param>
    public async Task FailJoinSessionAsync(string joinId, string reason, CancellationToken cancellationToken = default)
    {
        var session = GetJoinSession(joinId);
        if (session == null) return;

        var failPayload = new { action = "join_failed", joinId, reason };

        await SendToJoinClientAsync(joinId, failPayload, cancellationToken);

        var lobby = lobbyService.GetLobby(session.LobbyConnectionData);
        if (lobby?.HostWebSocket is { State: WebSocketState.Open } hostWs)
            await WebSocketMessenger.SendAsync(hostWs, failPayload, cancellationToken);

        CleanupJoinSession(joinId);
    }

    #endregion

    #region Cleanup

    /// <summary>
    /// Removes all expired join sessions and discovery tokens older than two minutes.
    /// Intended to be called on a periodic background timer alongside
    /// <see cref="LobbyService.CleanupDeadLobbies"/>.
    /// </summary>
    public void CleanupExpiredSessions()
    {
        var now = DateTime.UtcNow;

        var expiredJoinIds = _joinSessions
            .Where(kvp => kvp.Value.ExpiresAtUtc < now)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var joinId in expiredJoinIds)
            CleanupJoinSession(joinId);

        var tokenCutoff = now.AddMinutes(-2);
        var expiredTokens = _discoveryMetadata
            .Where(kvp => kvp.Value.CreatedAt < tokenCutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var token in expiredTokens)
            _discoveryMetadata.TryRemove(token, out _);
    }

    /// <summary>
    /// Removes all join sessions and host discovery metadata associated with a lobby that has been closed.
    /// Called by <see cref="LobbyService"/> when a lobby is removed.
    /// </summary>
    /// <param name="lobby">The lobby being removed.</param>
    internal void CleanupSessionsForLobby(_Lobby lobby)
    {
        var joinIds = _joinSessions.Values
            .Where(s => s.LobbyConnectionData == lobby.ConnectionData)
            .Select(s => s.JoinId)
            .ToList();

        foreach (var joinId in joinIds)
            CleanupJoinSession(joinId);

        if (!string.IsNullOrEmpty(lobby.HostDiscoveryToken))
            RemoveDiscoveryToken(lobby.HostDiscoveryToken);
    }

    #endregion

    #region Private helpers

    /// <summary>
    /// Handles a host UDP packet arriving: attempts to start all pending sessions for the lobby.
    /// </summary>
    private async Task HandleHostPortDiscoveredAsync(
        string lobbyConnectionData,
        int port,
        CancellationToken cancellationToken)
    {
        var lobby = lobbyService.GetLobby(lobbyConnectionData);
        if (lobby == null) return;

        lobby.ExternalPort = port;
        await SendToHostAsync(
            lobby,
            new {
                action = "host_mapping_received",
                hostPort = port,
                serverTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            },
            cancellationToken
        );
        await TryStartPendingJoinSessionsAsync(lobbyConnectionData, cancellationToken);
    }

    /// <summary>
    /// Handles a client UDP packet arriving: records the port, requests a host refresh,
    /// then attempts to start the session if the host port is already known.
    /// </summary>
    private async Task HandleClientPortDiscoveredAsync(string joinId, int port, CancellationToken cancellationToken)
    {
        var session = GetJoinSession(joinId);
        if (session == null) return;

        session.ClientExternalPort = port;
        await SendToJoinClientAsync(
            joinId,
            new {
                action = "client_mapping_received",
                clientPort = port,
                serverTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            },
            cancellationToken
        );

        var hostRefreshed = await SendHostRefreshRequestAsync(joinId, cancellationToken);
        if (!hostRefreshed)
        {
            await FailJoinSessionAsync(joinId, "host_unreachable", cancellationToken);
            return;
        }

        // Attempt to start immediately in case the host port was already known before this packet.
        await TryStartJoinSessionAsync(joinId, cancellationToken);
    }

    /// <summary>
    /// Iterates all pending sessions for a lobby and attempts to start each one.
    /// </summary>
    private async Task TryStartPendingJoinSessionsAsync(string lobbyConnectionData, CancellationToken cancellationToken)
    {
        var joinIds = _joinSessions.Values
            .Where(s => s.LobbyConnectionData == lobbyConnectionData)
            .Select(s => s.JoinId)
            .ToList();

        foreach (var joinId in joinIds)
            await TryStartJoinSessionAsync(joinId, cancellationToken);
    }

    /// <summary>
    /// Sends synchronized <c>start_punch</c> messages to both the client and the host when
    /// all required data (client port, host port, host WebSocket) is available.
    /// Silently returns when either port is still unknown; the method is retried automatically
    /// when the missing UDP packet arrives.
    /// </summary>
    private async Task TryStartJoinSessionAsync(string joinId, CancellationToken cancellationToken)
    {
        var session = GetJoinSession(joinId);
        if (session?.ClientExternalPort == null) return;

        var lobby = lobbyService.GetLobby(session.LobbyConnectionData);
        if (lobby == null)
        {
            await FailJoinSessionAsync(joinId, "lobby_closed", cancellationToken);
            return;
        }

        if (lobby.HostWebSocket is not { State: WebSocketState.Open } hostWs)
        {
            await FailJoinSessionAsync(joinId, "host_unreachable", cancellationToken);
            return;
        }

        if (lobby.ExternalPort == null) return;

        var hostIp = lobby.ConnectionData.Split(':')[0];
        var startTimeMs = DateTimeOffset.UtcNow.AddMilliseconds(250).ToUnixTimeMilliseconds();

        await SendToJoinClientAsync(
            joinId,
            new
            {
                action = "start_punch",
                joinId,
                hostIp,
                hostPort = lobby.ExternalPort.Value,
                startTimeMs
            },
            cancellationToken
        );

        await WebSocketMessenger.SendAsync(
            hostWs,
            new
            {
                action = "start_punch",
                joinId,
                clientIp = session.ClientIp,
                clientPort = session.ClientExternalPort.Value,
                hostPort = lobby.ExternalPort.Value,
                startTimeMs
            },
            cancellationToken
        );

        CleanupJoinSession(joinId);
    }

    /// <summary>
    /// Sends <paramref name="payload"/> to the client WebSocket for <paramref name="joinId"/>.
    /// Returns a completed task if the socket is unavailable.
    /// </summary>
    private Task SendToJoinClientAsync(string joinId, object payload, CancellationToken cancellationToken)
    {
        var session = GetJoinSession(joinId);
        return session?.ClientWebSocket is not { State: WebSocketState.Open } ws 
            ? Task.CompletedTask 
            : WebSocketMessenger.SendAsync(ws, payload, cancellationToken);
    }

    /// <summary>
    /// Sends <paramref name="payload"/> to the host WebSocket for <paramref name="lobby"/>,
    /// returning a completed task if the socket is unavailable.
    /// </summary>
    private static Task SendToHostAsync(_Lobby lobby, object payload, CancellationToken cancellationToken)
    {
        return lobby.HostWebSocket is not { State: WebSocketState.Open } ws
            ? Task.CompletedTask
            : WebSocketMessenger.SendAsync(ws, payload, cancellationToken);
    }

    /// <summary>
    /// Removes a join session and its client discovery token, then closes the client WebSocket
    /// if it is still open. Swallows close errors and logs them at debug level.
    /// </summary>
    private void CleanupJoinSession(string joinId)
    {
        if (!_joinSessions.TryRemove(joinId, out var session)) return;

        _discoveryMetadata.TryRemove(session.ClientDiscoveryToken, out _);

        if (session.ClientWebSocket is not { State: WebSocketState.Open } ws) return;

        try
        {
            ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "join complete", CancellationToken.None)
              .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to close client join WebSocket for join {JoinId}", joinId);
        }
    }

    #endregion
}
