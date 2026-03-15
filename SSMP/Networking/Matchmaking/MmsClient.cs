using System;
using System.Collections.Generic;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SSMP.Logging;

namespace SSMP.Networking.Matchmaking;

/// <summary>
/// High-performance client for the MatchMaking Service (MMS) API.
/// Orchestrates lobby creation, lookup, heartbeat, and NAT hole-punching.
///
/// Responsibilities intentionally kept here:
/// <list type="bullet">
///   <item>Lobby state (token, ID, connected-player count)</item>
///   <item>Heartbeat timer</item>
///   <item>Public API surface (Create, Register, Join, Close, …)</item>
/// </list>
/// Everything else is delegated to focused collaborators:
/// <see cref="MmsHttpClient"/>, <see cref="MmsWebSocketHandler"/>, <see cref="UdpDiscoveryService"/>.
/// </summary>
internal class MmsClient {
    /// <summary>Base URL of the MMS API.</summary>
    private readonly string _baseUrl;

    /// <summary>Hostname extracted from the base URL for UDP discovery.</summary>
    private readonly string? _discoveryHost;

    /// <summary>HTTP client for API requests.</summary>
    private readonly MmsHttpClient _http = new();

    /// <summary>WebSocket handler for host-side push events.</summary>
    private readonly MmsWebSocketHandler _webSocket;

    /// <summary>Current host token for the active lobby.</summary>
    private string? _hostToken;

    /// <summary>ID of the currently active lobby.</summary>
    private string? _currentLobbyId;

    /// <summary>Timer for sending periodic heartbeats to MMS.</summary>
    private Timer? _heartbeatTimer;

    /// <summary>Number of currently connected remote players.</summary>
    private int _connectedPlayers;

    /// <summary>Cancellation source for the active host discovery refresh loop, if any.</summary>
    private CancellationTokenSource? _hostDiscoveryRefreshCts;

    /// <summary>The last matchmaking error from the most recent operation.</summary>
    public MatchmakingError LastMatchmakingError =>
        _localError != MatchmakingError.None ? _localError : _http.LastError;

    /// <summary>Internal error state for non-HTTP failures.</summary>
    private MatchmakingError _localError = MatchmakingError.None;

    /// <inheritdoc cref="MmsWebSocketHandler.RefreshHostMappingRequested"/>
    public event Action<string, string, long>? RefreshHostMappingRequested {
        add => _webSocket.RefreshHostMappingRequested += value;
        remove => _webSocket.RefreshHostMappingRequested -= value;
    }

    /// <inheritdoc cref="MmsWebSocketHandler.HostMappingReceived"/>
    public event Action? HostMappingReceived {
        add => _webSocket.HostMappingReceived += value;
        remove => _webSocket.HostMappingReceived -= value;
    }

    /// <inheritdoc cref="MmsWebSocketHandler.StartPunchRequested"/>
    public event Action<string, string, int, int, long>? StartPunchRequested {
        add => _webSocket.StartPunchRequested += value;
        remove => _webSocket.StartPunchRequested -= value;
    }

    public MmsClient(string baseUrl) {
        _baseUrl = baseUrl.TrimEnd('/');
        _webSocket = new MmsWebSocketHandler(ToWebSocketUrl(_baseUrl));

        if (Uri.TryCreate(_baseUrl, UriKind.Absolute, out var uri))
            _discoveryHost = uri.Host;
    }

    #region Connected-player tracking

    /// <summary>
    /// Updates the number of connected remote players.
    /// Immediately sends a heartbeat if the count changed and a lobby is active,
    /// so MMS can clear stale host mappings as soon as the last player disconnects.
    /// </summary>
    public void SetConnectedPlayers(int count) {
        var normalized = System.Math.Max(0, count);
        if (_connectedPlayers == normalized) return;

        _connectedPlayers = normalized;
        if (_hostToken != null) SendHeartbeat(state: null);
    }

    #endregion

    #region Lobby lifecycle

    /// <summary>
    /// Creates a new lobby on MMS and starts the heartbeat and host WebSocket.
    /// </summary>
    /// <returns>Lobby code, lobby name, and host discovery token; all null on failure.</returns>
    public async Task<(string? lobbyCode, string? lobbyName, string? hostDiscoveryToken)> CreateLobbyAsync(
        int hostPort,
        bool isPublic = true,
        string gameVersion = "unknown",
        PublicLobbyType lobbyType = PublicLobbyType.Matchmaking
    ) {
        ClearErrors();

        var localIp = GetLocalIpAddress();
        var (buffer, length) = MmsJsonParser.FormatCreateLobbyJson(hostPort, isPublic, gameVersion, lobbyType, localIp);
        try {
            var json = new string(buffer, 0, length);
            var (success, response) = await _http.PostJsonAsync($"{_baseUrl}/lobby", json);
            if (!success || response == null) return (null, null, null);

            var span = response.AsSpan();
            var lobbyId = MmsJsonParser.ExtractValue(span, "connectionData");
            var hostToken = MmsJsonParser.ExtractValue(span, "hostToken");
            var lobbyName = MmsJsonParser.ExtractValue(span, "lobbyName");
            var lobbyCode = MmsJsonParser.ExtractValue(span, "lobbyCode");
            var hostDiscoveryToken = MmsJsonParser.ExtractValue(span, "hostDiscoveryToken");

            if (lobbyId == null || hostToken == null || lobbyName == null || lobbyCode == null) {
                Logger.Error($"MmsClient: invalid CreateLobby response: {response}");
                return (null, null, null);
            }

            ActivateLobby(lobbyId, hostToken);
            Logger.Info($"MmsClient: created lobby {lobbyCode}, token {hostDiscoveryToken}");
            return (lobbyCode, lobbyName, hostDiscoveryToken);
        } catch (Exception ex) {
            Logger.Error($"MmsClient: CreateLobbyAsync failed: {ex.Message}");
            return (null, null, null);
        } finally {
            MmsJsonParser.ReturnBuffer(buffer);
        }
    }

    /// <summary>
    /// Registers an existing Steam lobby with MMS for discovery.
    /// </summary>
    /// <returns>MMS lobby code, or null on failure.</returns>
    public async Task<string?> RegisterSteamLobbyAsync(
        string steamLobbyId,
        bool isPublic = true,
        string gameVersion = "unknown"
    ) {
        ClearErrors();

        try {
            var json =
                $"{{\"ConnectionData\":\"{steamLobbyId}\",\"IsPublic\":{BoolJson(isPublic)}," +
                $"\"GameVersion\":\"{gameVersion}\",\"LobbyType\":\"steam\"}}";

            var (success, response) = await _http.PostJsonAsync($"{_baseUrl}/lobby", json);
            if (!success || response == null) return null;

            var span = response.AsSpan();
            var lobbyId = MmsJsonParser.ExtractValue(span, "connectionData");
            var hostToken = MmsJsonParser.ExtractValue(span, "hostToken");
            var lobbyName = MmsJsonParser.ExtractValue(span, "lobbyName");
            var lobbyCode = MmsJsonParser.ExtractValue(span, "lobbyCode");

            if (lobbyId == null || hostToken == null || lobbyName == null || lobbyCode == null) {
                Logger.Error($"MmsClient: invalid RegisterSteamLobby response: {response}");
                return null;
            }

            ActivateLobby(lobbyId, hostToken);
            Logger.Info($"MmsClient: registered Steam lobby {steamLobbyId} as MMS lobby {lobbyCode}");
            return lobbyCode;
        } catch (TaskCanceledException) {
            Logger.Warn("MmsClient: Steam lobby registration cancelled");
            return null;
        } catch (Exception ex) {
            Logger.Warn($"MmsClient: RegisterSteamLobbyAsync failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Closes the active lobby and deregisters it from MMS.</summary>
    public void CloseLobby() {
        if (_hostToken == null) return;

        StopHeartbeat();
        StopHostDiscoveryRefresh();
        _webSocket.Stop();

        var tokenSnapshot = _hostToken;
        _hostToken = null;
        _currentLobbyId = null;

        // Best-effort DELETE, we do not want to block the calling thread.
        _ = SafeDeleteLobbyAsync(tokenSnapshot);
    }

    /// <summary>
    /// Performs a best-effort DELETE request to MMS to remove the lobby.
    /// </summary>
    /// <param name="hostToken">The host token of the lobby to delete.</param>
    private async Task SafeDeleteLobbyAsync(string hostToken) {
        try {
            await MmsHttpClient.DeleteAsync($"{_baseUrl}/lobby/{hostToken}");
            Logger.Info($"MmsClient: closed lobby {_currentLobbyId}");
        } catch (Exception ex) {
            Logger.Warn($"MmsClient: CloseLobby DELETE failed: {ex.Message}");
        }
    }

    #endregion

    #region Joining

    /// <summary>Looks up lobby join details from MMS.</summary>
    public async Task<JoinLobbyResult?> JoinLobbyAsync(string lobbyId, int clientPort) {
        ClearErrors();

        try {
            var json =
                $"{{\"ClientIp\":null,\"ClientPort\":{clientPort}," +
                $"\"MatchmakingVersion\":{MmsProtocol.CurrentVersion}}}";

            var (success, response) = await _http.PostJsonAsync($"{_baseUrl}/lobby/{lobbyId}/join", json);
            if (!success || response == null) return null;

            var span = response.AsSpan();
            var connectionData = MmsJsonParser.ExtractValue(span, "connectionData");
            var lobbyTypeString = MmsJsonParser.ExtractValue(span, "lobbyType");
            var lanConnectionData = MmsJsonParser.ExtractValue(span, "lanConnectionData");
            var clientDiscoveryToken = MmsJsonParser.ExtractValue(span, "clientDiscoveryToken");
            var joinId = MmsJsonParser.ExtractValue(span, "joinId");

            if (connectionData == null || lobbyTypeString == null) {
                Logger.Error($"MmsClient: invalid JoinLobby response: {response}");
                return null;
            }

            if (!Enum.TryParse(lobbyTypeString, true, out PublicLobbyType lobbyType)) {
                Logger.Error($"MmsClient: unknown lobby type '{lobbyTypeString}'");
                return null;
            }

            Logger.Info(
                $"MmsClient: joined lobby {lobbyId}, type={lobbyType}, connection={connectionData}, joinId={joinId}"
            );
            return new JoinLobbyResult {
                ConnectionData = connectionData,
                LobbyType = lobbyType,
                LanConnectionData = lanConnectionData,
                ClientDiscoveryToken = clientDiscoveryToken,
                JoinId = joinId
            };
        } catch (Exception ex) {
            Logger.Error($"MmsClient: JoinLobbyAsync failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Drives the client-side matchmaking WebSocket handshake.
    /// Sends UDP discovery packets to establish the NAT mapping, then waits
    /// for MMS to signal when both sides should begin simultaneous hole-punch.
    /// </summary>
    public async Task<MatchmakingJoinStartResult?> CoordinateMatchmakingJoinAsync(
        string joinId,
        Action<byte[], IPEndPoint> sendRawAction
    ) {
        ClearErrors();
        if (_discoveryHost == null)
            Logger.Warn("MmsClient: discovery host unknown; UDP mapping will be skipped");

        using var socket = new ClientWebSocket();
        using var timeoutCts =
            new CancellationTokenSource(TimeSpan.FromMilliseconds(MmsProtocol.MatchmakingWebSocketTimeoutMs));
        CancellationTokenSource? discoveryCts = null;
        Task? discoveryTask = null;

        try {
            var wsUrl = $"{ToWebSocketUrl(_baseUrl)}/ws/join/{joinId}?matchmakingVersion={MmsProtocol.CurrentVersion}";
            await socket.ConnectAsync(new Uri(wsUrl), timeoutCts.Token);

            var buffer = new byte[1024];
            while (socket.State == WebSocketState.Open && !timeoutCts.Token.IsCancellationRequested) {
                var result = await socket.ReceiveAsync(buffer, timeoutCts.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result is not { MessageType: WebSocketMessageType.Text, Count: > 0 }) continue;

                var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                var action = MmsJsonParser.ExtractValue(message.AsSpan(), "action");

                switch (action) {
                    case "begin_client_mapping":
                        discoveryCts = RestartDiscovery(
                            discoveryCts,
                            MmsJsonParser.ExtractValue(message.AsSpan(), "clientDiscoveryToken"),
                            sendRawAction
                        );
                        break;

                    case "start_punch":
                        discoveryCts?.Cancel();

                        var result2 = ParseStartPunch(message.AsSpan());
                        if (result2 == null) return null;

                        await DelayUntilAsync(result2.StartTimeMs, timeoutCts.Token);
                        return result2;

                    case "client_mapping_received":
                        discoveryCts?.Cancel();
                        break;

                    case "join_failed":
                        SetJoinFailed($"join_failed: {MmsJsonParser.ExtractValue(message.AsSpan(), "reason")}");
                        return null;
                }
            }
        } catch (OperationCanceledException) {
            SetJoinFailed("timeout");
        } catch (WebSocketException ex) {
            SetJoinFailed(ex.Message);
            Logger.Error($"MmsClient: matchmaking WebSocket error: {ex.Message}");
        } catch (Exception ex) {
            SetJoinFailed(ex.Message);
            Logger.Error($"MmsClient: CoordinateMatchmakingJoinAsync failed: {ex.Message}");
        } finally {
            discoveryCts?.Cancel();
            if (discoveryTask != null) await SafeAwaitAsync(discoveryTask);
            discoveryCts?.Dispose();
        }

        return null;
    }

    #endregion

    #region Public lobby browser

    /// <summary>
    /// Fetches a list of public lobbies from MMS.
    /// </summary>
    public async Task<List<PublicLobbyInfo>?> GetPublicLobbiesAsync(PublicLobbyType? lobbyType = null) {
        ClearErrors();

        try {
            var url = BuildPublicLobbiesUrl(lobbyType);
            var (success, response) = await _http.GetAsync(url);
            if (!success || response == null) return null;

            return ParsePublicLobbies(response);
        } catch (Exception ex) {
            Logger.Error($"MmsClient: GetPublicLobbiesAsync failed: {ex.Message}");
            return null;
        }
    }

    #endregion

    #region Compatibility probe

    /// <summary>
    /// Contacts MMS and verifies that its advertised matchmaking protocol version
    /// matches the client's expected version.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when MMS is reachable and compatible,
    /// <see langword="false"/> when MMS is reachable but requires an update,
    /// or <see langword="null"/> when MMS could not be contacted.
    /// </returns>
    public async Task<bool?> ProbeMatchmakingCompatibilityAsync() {
        ClearErrors();

        try {
            var (success, response) = await _http.GetAsync($"{_baseUrl}/");
            if (!success || response == null) {
                return null;
            }

            var versionString = MmsJsonParser.ExtractValue(response.AsSpan(), "version");
            if (!int.TryParse(versionString, out var serverVersion)) {
                _localError = MatchmakingError.NetworkFailure;
                Logger.Warn("MmsClient: MMS health response did not include a valid protocol version");
                return null;
            }

            if (serverVersion != MmsProtocol.CurrentVersion) {
                _localError = MatchmakingError.UpdateRequired;
                Logger.Warn(
                    $"MmsClient: MMS protocol mismatch (client={MmsProtocol.CurrentVersion}, server={serverVersion})"
                );
                return false;
            }

            return true;
        } catch (Exception ex) {
            _localError = MatchmakingError.NetworkFailure;
            Logger.Warn($"MmsClient: matchmaking compatibility probe failed: {ex.Message}");
            return null;
        }
    }

    #endregion

    #region Host-side discovery helpers

    /// <summary>
    /// Starts the WebSocket listener for host push events (pending clients / start-punch).
    /// Must be called after creating a lobby.
    /// </summary>
    public void StartPendingClientPolling() {
        if (_hostToken == null) {
            Logger.Error("MmsClient: cannot start WebSocket without a host token");
            return;
        }

        _webSocket.Start(_hostToken);
    }

    /// <summary>
    /// Fires off a background UDP discovery refresh for the given host token.
    /// Runs for up to <see cref="MmsProtocol.DiscoveryDurationSeconds"/> seconds.
    /// </summary>
    public void StartHostDiscoveryRefresh(string hostDiscoveryToken, Action<byte[], IPEndPoint> sendRawAction)
    {
        if (_discoveryHost == null) return;
 
        StopHostDiscoveryRefresh();

        _hostDiscoveryRefreshCts = new CancellationTokenSource(TimeSpan.FromSeconds(MmsProtocol.DiscoveryDurationSeconds));
        var cts = _hostDiscoveryRefreshCts;
 
        _ = UdpDiscoveryService
            .SendUntilCancelledAsync(_discoveryHost, hostDiscoveryToken, sendRawAction, cts.Token)
            .ContinueWith(_ =>
            {
                cts.Dispose();
                if (ReferenceEquals(_hostDiscoveryRefreshCts, cts))
                    _hostDiscoveryRefreshCts = null;
            }, TaskScheduler.Default);
    }

    /// <summary>
    /// Stops the active host discovery refresh loop, if one is running.
    /// </summary>
    public void StopHostDiscoveryRefresh() {
        _hostDiscoveryRefreshCts?.Cancel();
        _hostDiscoveryRefreshCts?.Dispose();
        _hostDiscoveryRefreshCts = null;
    }

    #endregion

    #region Private helpers

    /// <summary>
    /// Updates the internal lobby state and initiates the heartbeat timer.
    /// </summary>
    /// <param name="lobbyId">The ID of the lobby to activate.</param>
    /// <param name="hostToken">The host token for authentication.</param>
    private void ActivateLobby(string lobbyId, string hostToken) {
        _hostToken = hostToken;
        _currentLobbyId = lobbyId;
        StartHeartbeat();
    }

    /// <summary>
    /// Starts the periodic heartbeat timer.
    /// </summary>
    private void StartHeartbeat() {
        StopHeartbeat();
        _heartbeatTimer = new Timer(
            SendHeartbeat, null, MmsProtocol.HeartbeatIntervalMs, MmsProtocol.HeartbeatIntervalMs
        );
    }

    /// <summary>
    /// Stops and disposes of the heartbeat timer.
    /// </summary>
    private void StopHeartbeat() {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
    }

    /// <summary>
    /// Sends a heartbeat request to MMS. Triggered by <see cref="_heartbeatTimer"/>.
    /// </summary>
    /// <param name="state">Optional timer state.</param>
    private void SendHeartbeat(object? state) {
        if (_hostToken == null) return;
        var token = _hostToken;
        var players = _connectedPlayers;

        // Fire-and-forget, timer callback must not block.
        _ = _http.PostJsonAsync(
            $"{_baseUrl}/lobby/heartbeat/{token}",
            $"{{\"ConnectedPlayers\":{players}}}"
        );
    }

    /// <summary>
    /// Cancels any active discovery process and starts a new one with the provided token.
    /// </summary>
    /// <param name="existing">The existing cancellation token source to cancel.</param>
    /// <param name="token">The discovery token to send.</param>
    /// <param name="sendRaw">The callback for sending raw UDP packets.</param>
    /// <returns>A new <see cref="CancellationTokenSource"/> for the new discovery process.</returns>
    private CancellationTokenSource RestartDiscovery(
        CancellationTokenSource? existing,
        string? token,
        Action<byte[], IPEndPoint> sendRaw)
    {
        existing?.Cancel();
 
        if (string.IsNullOrEmpty(token) || _discoveryHost == null)
            return new CancellationTokenSource();
 
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(MmsProtocol.DiscoveryDurationSeconds));
        _ = UdpDiscoveryService.SendUntilCancelledAsync(_discoveryHost, token, sendRaw, cts.Token);
        return cts;
    }

    /// <summary>
    /// Parses the <c>start_punch</c> parameters from a JSON message.
    /// </summary>
    /// <param name="span">The JSON message content.</param>
    /// <returns>A <see cref="MatchmakingJoinStartResult"/> if successful; otherwise, <c>null</c>.</returns>
    private static MatchmakingJoinStartResult? ParseStartPunch(ReadOnlySpan<char> span) {
        var hostIp = MmsJsonParser.ExtractValue(span, "hostIp");
        var hostPortStr = MmsJsonParser.ExtractValue(span, "hostPort");
        var startTimeStr = MmsJsonParser.ExtractValue(span, "startTimeMs");

        if (hostIp == null ||
            !int.TryParse(hostPortStr, out var hostPort) ||
            !long.TryParse(startTimeStr, out var startTimeMs)) {
            return null;
        }

        return new MatchmakingJoinStartResult { HostIp = hostIp, HostPort = hostPort, StartTimeMs = startTimeMs };
    }

    /// <summary>
    /// Parses the public lobby list response from MMS.
    /// </summary>
    /// <param name="response">The HTTP response body.</param>
    /// <returns>A list of public lobbies.</returns>
    private static List<PublicLobbyInfo> ParsePublicLobbies(string response) {
        var result = new List<PublicLobbyInfo>();
        var span = response.AsSpan();
        var idx = 0;

        while (idx < span.Length) {
            var connStart = span[idx..].IndexOf("\"connectionData\":");
            if (connStart == -1) break;

            connStart += idx;
            var slice = span[connStart..];
            var connectionData = MmsJsonParser.ExtractValue(slice, "connectionData");
            var name = MmsJsonParser.ExtractValue(slice, "name");
            var typeString = MmsJsonParser.ExtractValue(slice, "lobbyType");
            var code = MmsJsonParser.ExtractValue(slice, "lobbyCode");

            var type = PublicLobbyType.Matchmaking;
            if (typeString != null) Enum.TryParse(typeString, true, out type);

            if (connectionData != null && name != null)
                result.Add(new PublicLobbyInfo(connectionData, name, type, code ?? ""));

            idx = connStart + 1;
        }

        return result;
    }

    /// <summary>
    /// Builds the URL for fetching public lobbies.
    /// </summary>
    /// <param name="lobbyType">Optional lobby type filter.</param>
    /// <returns>The URL string.</returns>
    private string BuildPublicLobbiesUrl(PublicLobbyType? lobbyType) {
        var url = $"{_baseUrl}/lobbies";
        if (lobbyType == null) return url;

        url += $"?type={lobbyType.ToString()!.ToLowerInvariant()}";
        if (lobbyType == PublicLobbyType.Matchmaking)
            url += $"&matchmakingVersion={MmsProtocol.CurrentVersion}";

        return url;
    }

    /// <summary>
    /// Signals a join failure with a specific reason.
    /// </summary>
    /// <param name="reason">The reason for failure.</param>
    private void SetJoinFailed(string reason) {
        Logger.Warn($"MmsClient: matchmaking join failed – {reason}");
        _localError = MatchmakingError.JoinFailed;
    }

    /// <summary>
    /// Clears the internal and HTTP error states.
    /// </summary>
    private void ClearErrors() {
        _localError = MatchmakingError.None;
        _http.ClearError();
    }

    #endregion

    #region Static utilities

    /// <summary>
    /// Determines the machine's LAN IP by routing toward an external address.
    /// No actual packet is sent; the socket is used only to resolve the local interface.
    /// </summary>
    private static string? GetLocalIpAddress() {
        try {
            using var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Dgram, 0
            );
            socket.Connect("8.8.8.8", 65530);
            return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString();
        } catch {
            return null;
        }
    }

    /// <summary>
    /// Asynchronously waits until the specified target Unix timestamp (ms).
    /// </summary>
    private static async Task DelayUntilAsync(long targetUnixMs, CancellationToken ct) {
        var delayMs = targetUnixMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (delayMs > 0) await Task.Delay(TimeSpan.FromMilliseconds(delayMs), ct);
    }

    /// <summary>
    /// Safely awaits a task, swallowing any exceptions.
    /// </summary>
    private static async Task SafeAwaitAsync(Task task) {
        try {
            await task;
        } catch {
            /* background task; failures are expected on cancellation */
        }
    }

    /// <summary>
    /// Converts an HTTP URL to a WebSocket URL.
    /// </summary>
    private static string ToWebSocketUrl(string httpUrl) =>
        httpUrl.Replace("http://", "ws://").Replace("https://", "wss://");

    /// <summary>
    /// Converts a boolean to its JSON string representation.
    /// </summary>
    private static string BoolJson(bool value) => value ? "true" : "false";

    #endregion
}
