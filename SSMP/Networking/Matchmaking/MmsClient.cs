using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SSMP.Logging;

namespace SSMP.Networking.Matchmaking;

/// <summary>
/// Client for the MatchMaking Service (MMS) API.
/// Handles lobby creation, lookup, heartbeat, and NAT hole-punch coordination.
/// </summary>
internal sealed class MmsClient : IDisposable {
    /// <summary>UDP port on the MMS server used for endpoint discovery.</summary>
    private const int UdpDiscoveryPort = 5001;

    /// <summary>Interval between heartbeat requests. Keeps the lobby alive on the MMS.</summary>
    private const int HeartbeatIntervalMs = 30_000;

    /// <summary>HTTP request timeout. Prevents hanging on an unresponsive server.</summary>
    private const int HttpTimeoutMs = 5_000;

    /// <summary>Opcode for UDP endpoint discovery packets: <c>[0x44][16-byte Guid]</c>.</summary>
    private const byte DiscoveryOpcode = 0x44; // 'D'

    /// <summary>Resend time for each packet.</summary>
    private const int DiscoveryRetryMs = 500;

    /// <summary>
    /// Shared, connection-pooled client. Do NOT dispose per-request.
    /// Configured for minimal overhead: no cookies, no proxy, no redirects.
    /// <para>
    ///   NOTE: The <see cref="ServicePointManager"/> settings applied in
    ///   <see cref="CreateSharedHttpClient"/> are process-wide. In Unity Mono they are
    ///   required for proper connection pooling. Be aware if other HTTP clients coexist
    ///   in the same process.
    /// </para>
    /// </summary>
    private static readonly HttpClient HttpClient = CreateSharedHttpClient();

    /// <summary>
    /// Pre-built content for heartbeat POSTs. Constructed once, reused forever.
    /// Sending an empty JSON object is sufficient since the token is in the URL.
    /// </summary>
    private static readonly ByteArrayContent EmptyJsonContent = CreateEmptyJsonContent();

    /// <summary>
    /// Creates and returns a process-wide HttpClient preconfigured for the MMS client's needs (tuned for Unity/Mono environments).
    /// </summary>
    /// <returns>An HttpClient instance with a non-proxy HttpClientHandler (no cookies, no redirects) and a timeout set to the client's configured HTTP timeout.</returns>
    private static HttpClient CreateSharedHttpClient() {
        // Process-wide on Mono/Unity, documented intentionally (see XML doc above)
        ServicePointManager.DefaultConnectionLimit = 10;
        ServicePointManager.UseNagleAlgorithm = false;
        ServicePointManager.Expect100Continue = false;

        var handler = new HttpClientHandler {
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false,
        };

        return new HttpClient(handler) {
            Timeout = TimeSpan.FromMilliseconds(HttpTimeoutMs)
        };
    }

    /// <summary>
    /// Creates a reusable HTTP content representing an empty JSON object.
    /// </summary>
    /// <returns>A <see cref="ByteArrayContent"/> containing "{}" with the Content-Type "application/json".</returns>
    private static ByteArrayContent CreateEmptyJsonContent() {
        var content = new ByteArrayContent("{}"u8.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private readonly string _baseUrl;

    private string? _hostToken;
    private string? _currentLobbyCode;

    private Timer? _heartbeatTimer;
    private ClientWebSocket? _hostWebSocket;
    private CancellationTokenSource? _webSocketCts;

    private bool _disposed;

    /// <summary>
    /// Raised when a pending client needs NAT hole-punching.
    /// Subscribe through an <see cref="MmsClient"/> instance.
    /// <para>
    ///   Parameters: (clientIp, clientPort). The caller should send UDP packets
    ///   to that endpoint from the gameplay socket to initiate hole punching.
    /// </para>
    /// </summary>
    public event Action<string, int>? PunchClientRequested;

    /// <summary>
    /// Static relay for callers that cannot hold an <see cref="MmsClient"/> instance reference.
    /// The active instance routes its push notifications here automatically.
    /// Prefer subscribing to <see cref="PunchClientRequested"/> directly when possible,
    /// as static events require manual unsubscription to avoid memory leaks.
    /// </summary>
    public static event Action<string, int>? PunchClientRequestedStatic;

    /// <summary>Initialises a new <see cref="MmsClient"/> targeting the given server URL.</summary>
    /// <summary>
    /// Initializes a new MmsClient configured to communicate with the given MMS server.
    /// </summary>
    /// <param name="baseUrl">Base URL of the MMS server (e.g. <c>"https://your-server:5000"</c>). Trailing slashes are removed.</param>
    public MmsClient(string baseUrl = "https://localhost:5000") {
        _baseUrl = baseUrl.TrimEnd('/');
    }

    /// <summary>
    /// Stops the heartbeat timer and WebSocket connection, then releases all managed resources.
    /// <summary>
    /// Releases resources used by the client, stops heartbeat and WebSocket background activity, and marks the instance as disposed.
    /// </summary>
    public void Dispose() {
        if (_disposed) return;
        _disposed = true;

        StopHeartbeat();
        StopWebSocket();
    }

    /// <summary>
    /// Creates a matchmaking lobby. The caller must have already called
    /// <see cref="SendDiscoveryPacketAsync"/> with <paramref name="discoveryToken"/> so the MMS
    /// can record the host's external endpoint before this request arrives.
    /// </summary>
    /// <param name="discoveryToken">Token sent in the UDP discovery packet to identify this host.</param>
    /// <param name="localPort">Host's local port, used to format the LAN IP for same-network detection.</param>
    /// <param name="isPublic">Whether to list in the public lobby browser.</param>
    /// <param name="gameVersion">Game version string for compatibility filtering.</param>
    /// <param name="lobbyType">Type of lobby to create.</param>
    /// <summary>
    /// Requests creation of a new lobby on the MMS server and applies returned host state on success.
    /// </summary>
    /// <param name="discoveryToken">Unique token used for UDP discovery/hole-punch coordination.</param>
    /// <param name="localPort">Local UDP port to advertise for LAN clients when a local IP is discoverable.</param>
    /// <param name="isPublic">Whether the lobby should be listed in public lobby queries.</param>
    /// <param name="gameVersion">Game version string to attach to the lobby metadata.</param>
    /// <param name="lobbyType">Type of the lobby to register (e.g., matchmaking or steam).</param>
    /// <returns>`(LobbyCode, LobbyName)` on success; `(null, null)` on failure.</returns>
    public async Task<(string? LobbyCode, string? LobbyName)> CreateLobbyAsync(
        Guid discoveryToken,
        int localPort = 26960,
        bool isPublic = true,
        string gameVersion = "unknown",
        PublicLobbyType lobbyType = PublicLobbyType.Matchmaking
    ) {
        try {
            var localIp = GetLocalIpAddress();
            var lanIpPart = localIp is not null ? $",\"HostLanIp\":\"{EscapeJsonString(localIp)}:{localPort}\"" : "";
            var typeString = lobbyType.ToWireString();
            var json =
                $"{{\"DiscoveryToken\":\"{discoveryToken}\"," +
                $"\"IsPublic\":{(isPublic ? "true" : "false")}," +
                $"\"GameVersion\":\"{EscapeJsonString(gameVersion)}\"," +
                $"\"LobbyType\":\"{EscapeJsonString(typeString)}\"{lanIpPart}}}";

            Logger.Info($"MmsClient: Creating lobby (token={discoveryToken}, localIp={localIp})");

            var response = await PostJsonStringAsync($"{_baseUrl}/lobby", json);
            if (response is null) return (null, null);

            if (!TryApplyLobbyCreationResponse(
                    response, nameof(CreateLobbyAsync), out var lobbyCode, out var lobbyName
                ))
                return (null, null);

            Logger.Info($"MmsClient: Created lobby {lobbyCode}");
            return (lobbyCode, lobbyName);
        } catch (Exception ex) {
            Logger.Error($"MmsClient: CreateLobby failed: {ex.Message}");
            return (null, null);
        }
    }

    /// <summary>
    /// Registers an existing Steam lobby with MMS for discovery.
    /// Call after creating a Steam lobby via <c>SteamMatchmaking.CreateLobby()</c>.
    /// </summary>
    /// <param name="steamLobbyId">The Steam lobby ID (<c>CSteamID</c> as string).</param>
    /// <param name="isPublic">Whether to list in the public lobby browser.</param>
    /// <param name="gameVersion">Game version string for compatibility filtering.</param>
    /// <summary>
    /// Register a Steam lobby with the MatchMaking Service (MMS) and obtain the assigned MMS lobby code.
    /// </summary>
    /// <param name="steamLobbyId">The Steam lobby identifier to register as the connection data.</param>
    /// <param name="isPublic">Whether the registered lobby should be visible in public listings.</param>
    /// <param name="gameVersion">A string identifying the game/version to advertise for the lobby.</param>
    /// <returns>The MMS lobby code on success; <see langword="null"/> on failure.</returns>
    public async Task<string?> RegisterSteamLobbyAsync(
        string steamLobbyId,
        bool isPublic = true,
        string gameVersion = "unknown"
    ) {
        try {
            var json =
                $"{{\"ConnectionData\":\"{EscapeJsonString(steamLobbyId)}\"," +
                $"\"IsPublic\":{(isPublic ? "true" : "false")}," +
                $"\"GameVersion\":\"{EscapeJsonString(gameVersion)}\"," +
                $"\"LobbyType\":\"steam\"}}";

            var response = await PostJsonStringAsync($"{_baseUrl}/lobby", json);
            if (response is null) return null;

            if (!TryApplyLobbyCreationResponse(response, nameof(RegisterSteamLobbyAsync), out var lobbyCode, out _))
                return null;

            Logger.Info($"MmsClient: Registered Steam lobby {steamLobbyId} as {lobbyCode}");
            return lobbyCode;
        } catch (Exception ex) {
            Logger.Warn($"MmsClient: RegisterSteamLobby failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Closes the currently hosted lobby on the MMS.
    /// Stops the heartbeat and WebSocket synchronously, then fires the HTTP DELETE
    /// in the background so the calling thread (Unity main thread) is not blocked.
    /// <summary>
    /// Closes the current host lobby if one exists and clears the client's host state.
    /// </summary>
    /// <remarks>
    /// Stops the heartbeat and any active WebSocket polling, clears the stored host token and lobby code, and asynchronously requests the server to deregister the lobby. If there is no active host token this method does nothing. Outcomes of the server request are logged; the method does not throw for server errors.
    /// </remarks>
    public void CloseLobby() {
        if (_hostToken is null) return;

        StopHeartbeat();
        StopWebSocket();

        // Capture before clearing so the background task captures the right values.
        var token = _hostToken;
        var code = _currentLobbyCode;
        _hostToken = null;
        _currentLobbyCode = null;

        _ = Task.Run(async () => {
            try {
                if (await DeleteAsync($"{_baseUrl}/lobby/{token}")) Logger.Info($"MmsClient: Closed lobby {code}");
                else Logger.Warn($"MmsClient: CloseLobby DELETE returned false for lobby {code}");
            } catch (Exception ex) {
                Logger.Warn($"MmsClient: CloseLobby failed: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Returns the list of public lobbies, optionally filtered by type.
    /// </summary>
    /// <param name="lobbyType">Optional filter. Pass <see langword="null"/> to return all types.</param>
    /// <summary>
    /// Retrieves the list of public lobbies, optionally filtered by lobby type.
    /// </summary>
    /// <param name="lobbyType">Optional lobby type to filter results; when null, returns lobbies of all types.</param>
    /// <returns>A list of PublicLobbyInfo for available lobbies, or <see langword="null"/> if the request failed.</returns>
    public async Task<List<PublicLobbyInfo>?> GetPublicLobbiesAsync(PublicLobbyType? lobbyType = null) {
        try {
            var url = lobbyType is null
                ? $"{_baseUrl}/lobbies"
                : $"{_baseUrl}/lobbies?type={lobbyType.Value.ToWireString()}";

            var response = await GetJsonAsync(url);
            return response is null ? null : ParseLobbyList(response.AsSpan());
        } catch (Exception ex) {
            Logger.Error($"MmsClient: GetPublicLobbies failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Joins a lobby and returns host connection info plus a client token for UDP discovery.
    /// After calling this, invoke <see cref="SendDiscoveryPacketAsync"/> with the returned
    /// <c>ClientToken</c> from the gameplay socket to initiate NAT hole-punching.
    /// </summary>
    /// <param name="lobbyId">The lobby code or connection data to join.</param>
    /// <returns>
    /// A tuple of host connection details and a client token on success;
    /// <see langword="null"/> on failure.
    /// <summary>
    /// Attempts to join the lobby with the specified ID and, on success, returns the connection and session details.
    /// </summary>
    /// <param name="lobbyId">The MMS lobby identifier to join.</param>
    /// <returns>
    /// A tuple containing:
    /// - `ConnectionData`: the host connection string to use for gameplay.
    /// - `LobbyType`: the lobby's PublicLobbyType value.
    /// - `LanConnectionData`: an optional LAN connection string, or `null` if not provided.
    /// - `ClientToken`: a GUID token issued to this client for the session.
    /// Returns `null` if the join request failed or the response was invalid.
    /// </returns>
    public async Task<(string ConnectionData, PublicLobbyType LobbyType, string? LanConnectionData, Guid ClientToken)?>
        JoinLobbyAsync(string lobbyId) {
        try {
            var response = await PostJsonStringAsync($"{_baseUrl}/lobby/{lobbyId}/join", "{}");
            if (response is null) return null;

            var span = response.AsSpan();

            var connectionData = ExtractJsonValue(span, "connectionData");
            var lobbyTypeString = ExtractJsonValue(span, "lobbyType");
            var lanConnectionData = ExtractJsonValue(span, "lanConnectionData");
            var clientTokenString = ExtractJsonValue(span, "clientToken");

            if (connectionData is null || lobbyTypeString is null || clientTokenString is null) {
                Logger.Error($"MmsClient: Unexpected JoinLobby response: {response}");
                return null;
            }

            if (!Enum.TryParse(lobbyTypeString, ignoreCase: true, out PublicLobbyType lobbyType)) {
                Logger.Error($"MmsClient: Unknown lobby type '{lobbyTypeString}'");
                return null;
            }

            if (!Guid.TryParse(clientTokenString, out var clientToken)) {
                Logger.Error($"MmsClient: Invalid client token '{clientTokenString}'");
                return null;
            }

            Logger.Info($"MmsClient: Joined lobby {lobbyId} [{lobbyType}] -> {connectionData}");
            return (connectionData, lobbyType, lanConnectionData, clientToken);
        } catch (Exception ex) {
            Logger.Error($"MmsClient: JoinLobby failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Sends UDP discovery packets from the given socket in a loop until <paramref name="ct"/>
    /// is canceled, so the MMS can record this socket's external endpoint.
    /// Must be called from the same socket used for gameplay so the observed external
    /// endpoint matches the one peers need to reach.
    /// </summary>
    /// <remarks>
    /// Packet layout: <c>[0x44 'D'][16-byte Guid, little-endian]</c>.
    /// The packet is stack-allocated so no heap allocations occur.
    /// UDP is unreliable, so the packet is resent every <see cref="DiscoveryRetryMs"/> ms until
    /// canceled. The MMS records each packet idempotently.
    /// Cancel the token once the TCP response (lobby creation or join) is received.
    /// </remarks>
    /// <param name="socket">The bound UDP socket to send from.</param>
    /// <param name="token">Token that identifies this discovery attempt.</param>
    /// <summary>
    /// Repeatedly sends UDP discovery packets containing the provided discovery token to the MMS server's discovery endpoint until cancelled.
    /// </summary>
    /// <param name="socket">UDP socket used to send discovery packets.</param>
    /// <param name="token">Discovery token to include in each packet.</param>
    /// <param name="ct">Cancellation token that, when cancelled by the caller (typically when MMS has acknowledged the endpoint via TCP), stops the discovery loop.</param>
    public async Task SendDiscoveryPacketAsync(Socket socket, Guid token, CancellationToken ct) {
        IPEndPoint endpoint;
        try {
            var uri = new Uri(_baseUrl);
            var addresses = await Dns.GetHostAddressesAsync(uri.Host);
            if (addresses.Length == 0) {
                Logger.Warn("MmsClient: Could not resolve MMS hostname for UDP discovery");
                return;
            }
            endpoint = new IPEndPoint(addresses[0], UdpDiscoveryPort);
        } catch (Exception ex) {
            Logger.Warn($"MmsClient: Discovery packet setup failed: {ex.Message}");
            return;
        }

        var packetBytes = new byte[17];
        packetBytes[0] = DiscoveryOpcode;
        token.TryWriteBytes(packetBytes.AsSpan(1));

        Logger.Info($"MmsClient: Starting discovery packet loop to {endpoint} for token {token}");
        var sent = 0;
        while (!ct.IsCancellationRequested) {
            try {
                socket.SendTo(packetBytes, endpoint);
                sent++;
                Logger.Debug($"MmsClient: Sent discovery packet #{sent} to {endpoint}");
                await Task.Delay(DiscoveryRetryMs, ct);
            } catch (OperationCanceledException) {
                break;
            } catch (Exception ex) {
                Logger.Warn($"MmsClient: Discovery packet send failed: {ex.Message}");
            }
        }

        Logger.Info($"MmsClient: Discovery packet loop stopped after {sent} packet(s)");
    }

    /// <summary>
    /// Opens a WebSocket connection to the MMS to receive real-time client endpoint
    /// push notifications. Should be called immediately after creating a lobby.
    /// Runs the connection loop on the thread pool via fire-and-forget.
    /// <summary>
    /// Starts polling for pending clients by initiating the host WebSocket connection.
    /// </summary>
    /// <remarks>
    /// If the client is not a host (no host token available) the method logs an error and does nothing.
    /// The connection attempt is initiated without awaiting completion so the call returns immediately.
    /// </remarks>
    public void StartPendingClientPolling() {
        if (_hostToken is null) {
            Logger.Error("MmsClient: Cannot start WebSocket without host token");
            return;
        }
        _ = ConnectWebSocketAsync();
    }

    /// <summary>
    /// Connects the WebSocket, receives messages in a loop until cancellation or closure,
    /// then disposes the socket. Uses a rented buffer for the receive loop to avoid
    /// per-message allocation.
    /// <summary>
    /// Establishes a WebSocket connection for the current host token and listens for incoming text messages until the connection closes or cancellation is requested.
    /// </summary>
    /// <remarks>
    /// Runs until the socket is closed or the internal cancellation token is signaled; received text messages are passed to the message handler, and the socket is disposed when the method completes.
    /// </remarks>
    private async Task ConnectWebSocketAsync() {
        StopWebSocket();

        _webSocketCts = new CancellationTokenSource();
        _hostWebSocket = new ClientWebSocket();
        var ct = _webSocketCts.Token;

        var wsUrl = _baseUrl
                    .Replace("http://", "ws://", StringComparison.OrdinalIgnoreCase)
                    .Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase);

        try {
            await _hostWebSocket.ConnectAsync(new Uri($"{wsUrl}/ws/{_hostToken}"), ct);
            Logger.Info("MmsClient: WebSocket connected");

            var buffer = ArrayPool<byte>.Shared.Rent(1024);
            try {
                while (_hostWebSocket.State == WebSocketState.Open && !ct.IsCancellationRequested) {
                    var result = await _hostWebSocket.ReceiveAsync(
                        new ArraySegment<byte>(buffer), ct
                    );

                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    if (result is { MessageType: WebSocketMessageType.Text, Count: > 0 })
                        HandleWebSocketMessage(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
            } finally {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        } catch (OperationCanceledException) {
            // Normal shutdown via StopWebSocket
        } catch (Exception ex) {
            Logger.Error($"MmsClient: WebSocket error: {ex.Message}");
        } finally {
            // Always dispose here. StopWebSocket only cancels, never disposes directly,
            // to avoid ObjectDisposedException mid-ReceiveAsync
            _hostWebSocket?.Dispose();
            _hostWebSocket = null;
            Logger.Info("MmsClient: WebSocket disconnected");
        }
    }

    /// <summary>
    /// Parses an incoming WebSocket message and raises <see cref="PunchClientRequested"/>
    /// and <see cref="PunchClientRequestedStatic"/> if a valid client endpoint is found.
    /// </summary>
    /// <summary>
    /// Processes a JSON push message from the MMS server and, if it contains a valid client IP and port, raises NAT punch request events.
    /// </summary>
    /// <param name="message">UTF-8 JSON payload expected to contain the keys "clientIp" (string) and "clientPort" (integer); messages missing either key are ignored.</param>
    private void HandleWebSocketMessage(string message) {
        var span = message.AsSpan();
        var ip = ExtractJsonValue(span, "clientIp");
        var portStr = ExtractJsonValue(span, "clientPort");

        if (ip is not null && int.TryParse(portStr, out var port)) {
            Logger.Info($"MmsClient: Push notification - client {ip}:{port}");
            PunchClientRequested?.Invoke(ip, port);
            PunchClientRequestedStatic?.Invoke(ip, port);
        }
    }

    /// <summary>
    /// Cancels and disposes the active WebSocket cancellation token source.
    /// The receive loop in <see cref="ConnectWebSocketAsync"/> disposes the socket itself
    /// to avoid <see cref="ObjectDisposedException"/> mid-receive.
    /// <summary>
    /// Stops any active WebSocket activity by cancelling and disposing the cancellation token source and clearing its reference.
    /// </summary>
    private void StopWebSocket() {
        _webSocketCts?.Cancel();
        _webSocketCts?.Dispose();
        _webSocketCts = null;
    }

    /// <summary>Starts (or restarts) the periodic heartbeat timer.</summary>
    private void StartHeartbeat() {
        StopHeartbeat();
        _heartbeatTimer = new Timer(OnHeartbeatTick, null, HeartbeatIntervalMs, HeartbeatIntervalMs);
    }

    /// <summary>Stops and disposes the heartbeat timer if one is running.</summary>
    private void StopHeartbeat() {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
    }

    /// <summary>
    /// Timer callback that fires <see cref="SendHeartbeatAsync"/> as a fire-and-forget task.
    /// Never blocks the timer thread.
    /// </summary>
    /// <summary>
    /// Schedule sending a lobby heartbeat if this client is currently hosting a lobby.
    /// </summary>
    /// <param name="state">Timer state object passed by the timer callback; ignored by this method.</param>
    private void OnHeartbeatTick(object? state) {
        if (_hostToken is null) return;
        _ = SendHeartbeatAsync(_hostToken);
    }

    /// <summary>
    /// Posts a heartbeat to the MMS for the given host token.
    /// Uses the pre-built <see cref="EmptyJsonContent"/> to avoid per-call allocation.
    /// </summary>
    /// <summary>
    /// Sends a heartbeat to refresh the lobby registration for the specified host token.
    /// </summary>
    /// <param name="hostToken">The host token identifying the lobby to refresh.</param>
    /// <remarks>Logs a warning if the heartbeat request fails.</remarks>
    private async Task SendHeartbeatAsync(string hostToken) {
        try {
            await HttpClient.PostAsync($"{_baseUrl}/lobby/heartbeat/{hostToken}", EmptyJsonContent);
        } catch (Exception ex) {
            Logger.Warn($"MmsClient: Heartbeat failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Issues a GET request and returns the response body as a string.
    /// Returns <see langword="null"/> on a non-success status code or network error.
    /// </summary>
    /// <summary>
    /// Fetches the response body from the specified absolute URL when the HTTP response status is successful.
    /// </summary>
    /// <param name="url">Absolute URL to request.</param>
    /// <returns>The response body as a string if the request returns a success status code; otherwise <c>null</c> (including on network errors or timeouts).</returns>
    private static async Task<string?> GetJsonAsync(string url) {
        try {
            using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsStringAsync();
        } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) {
            return null;
        }
    }

    /// <summary>
    /// Issues a POST request with a raw JSON string body and returns the response body.
    /// Returns <see langword="null"/> on a non-success status code or network error.
    /// </summary>
    /// <param name="url">Absolute URL to POST to.</param>
    /// <summary>
    /// Sends a POST request with the provided JSON body to the specified URL and returns the response body when successful.
    /// </summary>
    /// <param name="url">The target request URL.</param>
    /// <param name="json">JSON string to send as the request body.</param>
    /// <returns>The response body as a string if the request returns a success status code; `null` on non-success status or network/timeout errors.</returns>
    private static async Task<string?> PostJsonStringAsync(string url, string json) {
        try {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await HttpClient.PostAsync(url, content);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsStringAsync();
        } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) {
            return null;
        }
    }

    /// <summary>
    /// Issues a DELETE request and returns whether it succeeded.
    /// Returns <see langword="false"/> on a non-success status code or network error.
    /// </summary>
    /// <summary>
    /// Send an HTTP DELETE request to the specified absolute URL and indicate whether it succeeded.
    /// </summary>
    /// <param name="url">Absolute URL to DELETE.</param>
    /// <returns>`true` if the response status code indicates success (2xx); `false` on non-success status or network/timeouts.</returns>
    private static async Task<bool> DeleteAsync(string url) {
        try {
            using var response = await HttpClient.DeleteAsync(url);
            return response.IsSuccessStatusCode;
        } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) {
            return false;
        }
    }

    /// <summary>
    /// Extracts <c>hostToken</c>, <c>lobbyCode</c>, and <c>lobbyName</c> from a lobby
    /// creation response, applies them to instance state, and starts the heartbeat timer.
    /// Shared by <see cref="CreateLobbyAsync"/> and <see cref="RegisterSteamLobbyAsync"/>
    /// to avoid duplicating identical post-processing logic.
    /// </summary>
    /// <param name="response">Raw JSON response body from a successful <c>POST /lobby</c> call.</param>
    /// <param name="callerName">
    /// Name of the calling method, used verbatim in the error log message when parsing fails.
    /// </param>
    /// <param name="lobbyCode">
    /// When this method returns <see langword="true"/>, contains the parsed lobby code;
    /// otherwise <see langword="null"/>.
    /// </param>
    /// <param name="lobbyName">
    /// When this method returns <see langword="true"/>, contains the parsed lobby name;
    /// otherwise <see langword="null"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if all three fields were present and state was updated;
    /// <see langword="false"/> if any field was missing (the unexpected response is logged at Error).
    /// <summary>
    /// Parses an MMS lobby creation response, applies host and lobby state, and starts the heartbeat timer when successful.
    /// </summary>
    /// <param name="response">The JSON response body from the lobby creation endpoint.</param>
    /// <param name="callerName">Short identifier of the caller used for logging in case of unexpected responses.</param>
    /// <param name="lobbyCode">On success, set to the parsed lobby code; otherwise null.</param>
    /// <param name="lobbyName">On success, set to the parsed lobby name; otherwise null.</param>
    /// <returns>`true` if `hostToken`, `lobbyCode`, and `lobbyName` were all present and applied; `false` otherwise.</returns>
    private bool TryApplyLobbyCreationResponse(
        string response,
        string callerName,
        out string? lobbyCode,
        out string? lobbyName
    ) {
        var span = response.AsSpan();
        var hostToken = ExtractJsonValue(span, "hostToken");
        lobbyCode = ExtractJsonValue(span, "lobbyCode");
        lobbyName = ExtractJsonValue(span, "lobbyName");

        if (hostToken is null || lobbyCode is null || lobbyName is null) {
            Logger.Error($"MmsClient: Unexpected {callerName} response: {response}");
            lobbyCode = null;
            lobbyName = null;
            return false;
        }

        _hostToken = hostToken;
        _currentLobbyCode = lobbyCode;
        StartHeartbeat();
        return true;
    }

    /// <summary>
    /// Parses a JSON array of lobby objects into a list.
    /// Advances the scan position after each parsed entry to avoid O(N²) re-scanning.
    /// </summary>
    /// <param name="json">The raw JSON span to parse.</param>
    /// <summary>
    /// Parses a JSON array of lobby objects and returns their public lobby information.
    /// </summary>
    /// <param name="json">A span containing the JSON array of lobby objects.</param>
    /// <returns>A list of <see cref="PublicLobbyInfo"/> entries for objects that contain the required connection data and name; entries missing those fields are skipped.</returns>
    private static List<PublicLobbyInfo> ParseLobbyList(ReadOnlySpan<char> json) {
        var result = new List<PublicLobbyInfo>();
        var remaining = json;

        while (true) {
            var entryStart = remaining.IndexOf("\"connectionData\":", StringComparison.Ordinal);
            if (entryStart == -1) break;

            var entry = remaining[entryStart..];
            var connectionData = ExtractJsonValue(entry, "connectionData");
            var name = ExtractJsonValue(entry, "name");
            var typeString = ExtractJsonValue(entry, "lobbyType");
            var code = ExtractJsonValue(entry, "lobbyCode");

            if (connectionData is not null && name is not null) {
                Enum.TryParse(typeString, ignoreCase: true, out PublicLobbyType lobbyType);
                result.Add(new PublicLobbyInfo(connectionData, name, lobbyType, code ?? ""));
            }

            // Advance past the current entry to avoid re-scanning processed data
            remaining = remaining[(entryStart + "\"connectionData\":".Length)..];
        }

        return result;
    }

    /// <summary>
    /// Extracts a string or numeric JSON value by key with zero heap allocations.
    /// Assumes well-formed JSON as produced by MMS responses.
    /// Searches for <c>"key":</c> and extracts the immediately following value.
    /// </summary>
    /// <param name="json">The JSON span to search.</param>
    /// <param name="key">The property name to locate (without quotes).</param>
    /// <returns>
    /// The extracted value as a <see cref="string"/> (quotes stripped for string values);
    /// <see langword="null"/> if the key is not found or the span is malformed.
    /// <summary>
    /// Extracts the value of the specified JSON property from a JSON text span.
    /// </summary>
    /// <param name="json">A span containing JSON text to search.</param>
    /// <param name="key">The JSON property name to locate (without quotes).</param>
    /// <returns>
    /// The property's value as a string with surrounding quotes removed for string values, or `null` if the property is not found or the value is malformed.
    /// </returns>
    private static string? ExtractJsonValue(ReadOnlySpan<char> json, string key) {
        // Build "key": on the stack to avoid a heap allocation for the search pattern
        Span<char> searchKey = stackalloc char[key.Length + 3];
        searchKey[0] = '"';
        key.AsSpan().CopyTo(searchKey[1..]);
        searchKey[key.Length + 1] = '"';
        searchKey[key.Length + 2] = ':';

        var idx = json.IndexOf(searchKey, StringComparison.Ordinal);
        if (idx == -1) return null;

        var valueStart = idx + searchKey.Length;
        while (valueStart < json.Length && char.IsWhiteSpace(json[valueStart]))
            valueStart++;

        if (valueStart >= json.Length) return null;

        if (json[valueStart] == '"') {
            // Escape-aware string scan that skips \" sequences instead of terminating on them
            var searchStart = valueStart + 1;
            while (searchStart < json.Length) {
                switch (json[searchStart])
                {
                    case '\\':
                        // skip the escaped character
                        searchStart += 2; 
                        continue;
                    case '"':
                        return json.Slice(valueStart + 1, searchStart - valueStart - 1).ToString();
                    default:
                        searchStart++;
                        break;
                }
            }
            return null;
        }

        // Unquoted value (number, bool, null) - read until JSON delimiter
        var valueEnd = valueStart;
        while (valueEnd < json.Length &&
               json[valueEnd] != ',' && json[valueEnd] != '}' && json[valueEnd] != ']')
            valueEnd++;

        return json.Slice(valueStart, valueEnd - valueStart).Trim().ToString();
    }

    /// <summary>
    /// Escapes a string for safe interpolation into a JSON string literal.
    /// Handles <c>\</c>, <c>"</c>, and control characters (<c>U+0000</c>–<c>U+001F</c>).
    /// </summary>
    /// <param name="value">The raw string to escape.</param>
    /// <summary>
    /// Escape special and control characters in a string for safe inclusion in a JSON string literal.
    /// </summary>
    /// <param name="value">The input string to escape.</param>
    /// <returns>The input string with characters escaped so it can be embedded between JSON double quotes.</returns>
    private static string EscapeJsonString(string value) {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value) {
            switch (c) {
                case '\\': sb.Append("\\\\"); break;
                case '\"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                default:
                    if (c < ' ')
                        sb.Append($"\\u{(int)c:X4}");
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Determines the machine's outbound-facing local IP without establishing a connection.
    /// Uses a dummy UDP <c>Connect</c> to select the correct routing interface.
    /// </summary>
    /// <remarks>
    /// The target address (<c>8.8.8.8:65530</c>) is irrelevant - no data is ever sent.
    /// All socket exceptions are intentionally swallowed; a missing LAN IP is non-fatal
    /// and results in same-network detection being skipped on the server side.
    /// </remarks>
    /// <summary>
    /// Determines the local IPv4 address used for outbound network connections by opening a temporary UDP socket.
    /// </summary>
    /// <returns>The local IP address string, or <see langword="null"/> if it cannot be determined.</returns>
    private static string? GetLocalIpAddress() {
        try {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 65530);
            return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString();
        } catch {
            return null;
        }
    }
}

/// <summary>Public lobby information returned by the lobby browser endpoint.</summary>
public record PublicLobbyInfo(
    string ConnectionData,
    string Name,
    PublicLobbyType LobbyType,
    string LobbyCode
);

/// <summary>Discriminates between the two supported lobby transports.</summary>
public enum PublicLobbyType {
    /// <summary>Standalone matchmaking through MMS with UDP hole-punching.</summary>
    Matchmaking,

    /// <summary>Steam matchmaking registered through MMS for discoverability.</summary>
    Steam
}

/// <summary>Extension methods for <see cref="PublicLobbyType"/>.</summary>
internal static class PublicLobbyTypeExtensions {
    /// <summary>
    /// Returns the lowercase wire-format string used in MMS API requests.
    /// Uses a switch expression rather than <c>ToString().ToLowerInvariant()</c>
    /// to avoid culture-sensitivity issues and unnecessary string allocation.
    /// </summary>
    /// <param name="type">The lobby type to convert.</param>
    /// <returns>
    /// <c>"matchmaking"</c> or <c>"steam"</c>; falls back to
    /// <c>ToString().ToLowerInvariant()</c> for unknown values.
    /// <summary>
    /// Converts a PublicLobbyType value to the canonical wire-format string used by the API.
    /// </summary>
    /// <returns>
    /// The wire-format string: "matchmaking" for Matchmaking, "steam" for Steam, or the enum name lowercased for other values.
    /// </returns>
    public static string ToWireString(this PublicLobbyType type) => type switch {
        PublicLobbyType.Matchmaking => "matchmaking",
        PublicLobbyType.Steam => "steam",
        _ => type.ToString().ToLowerInvariant()
    };
}
