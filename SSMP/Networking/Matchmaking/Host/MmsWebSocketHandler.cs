using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SSMP.Logging;
using SSMP.Networking.Matchmaking.Parsing;
using SSMP.Networking.Matchmaking.Protocol;

namespace SSMP.Networking.Matchmaking.Host;

/// <summary>
/// Manages the persistent WebSocket connection between a lobby host and MMS.
/// MMS pushes control events over this channel to coordinate matchmaking flow.
/// </summary>
internal sealed class MmsWebSocketHandler : IDisposable {
    /// <summary>The base WebSocket URL of the MMS service.</summary>
    private readonly string _wsBaseUrl;

    /// <summary>The underlying WebSocket client.</summary>
    private ClientWebSocket? _socket;

    /// <summary>Cancellation source for the background listening loop.</summary>
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Raised when MMS asks the host to refresh its NAT mapping.
    /// Arguments: joinId, hostDiscoveryToken, serverTimeMs.
    /// </summary>
    public event Action<string, string, long>? RefreshHostMappingRequested;

    /// <summary>
    /// Raised when MMS signals both sides to start simultaneous hole-punch.
    /// Arguments: joinId, clientIp, clientPort, hostPort, startTimeMs.
    /// </summary>
    public event Action<string, string, int, int, long>? StartPunchRequested;

    /// <summary>
    /// Raised when MMS confirms the host mapping has been learned and refresh
    /// packets can stop.
    /// </summary>
    public event Action? HostMappingReceived;

    /// <summary>
    /// Initialises a new <see cref="MmsWebSocketHandler"/>.
    /// </summary>
    /// <param name="wsBaseUrl">Base WebSocket URL of the MMS service (e.g. <c>wss://mms.example.com</c>).</param>
    public MmsWebSocketHandler(string wsBaseUrl) {
        _wsBaseUrl = wsBaseUrl;
    }

    /// <summary>
    /// Opens the WebSocket connection and begins listening for host push events.
    /// Any previously active connection is stopped first.
    /// Runs on a background thread; returns immediately.
    /// </summary>
    /// <param name="hostToken">Bearer token used to authenticate the WebSocket URL.</param>
    public void Start(string hostToken) {
        Stop();
        Task.Run(() => RunAsync(hostToken));
    }

    /// <summary>
    /// Cancels the listening loop and disposes the WebSocket connection.
    /// Safe to call when no connection is active.
    /// </summary>
    public void Stop() {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _socket?.Dispose();
        _socket = null;
    }

    /// <inheritdoc/>
    public void Dispose() => Stop();

    /// <summary>
    /// Entry point for the background task. Connects the socket, runs the
    /// receive loop, then tears down and logs the disconnection.
    /// </summary>
    /// <param name="hostToken">Bearer token used to build the WebSocket URL.</param>
    private async Task RunAsync(string hostToken) {
        _cts = new CancellationTokenSource();
        _socket = new ClientWebSocket();

        try {
            await ConnectAsync(hostToken);
            await ReceiveLoopAsync();
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            Logger.Error($"MmsWebSocketHandler: error - {ex.Message}");
        } finally {
            TearDownSocket();
        }
    }

    /// <summary>
    /// Connects <see cref="_socket"/> to the host WebSocket endpoint.
    /// </summary>
    /// <param name="hostToken">Token appended to the WebSocket URL path.</param>
    private async Task ConnectAsync(string hostToken) {
        var uri = new Uri($"{_wsBaseUrl}{MmsRoutes.HostWebSocket(hostToken)}");
        await _socket!.ConnectAsync(uri, _cts!.Token);
        Logger.Info("MmsWebSocketHandler: connected");
    }

    /// <summary>
    /// Reads messages from <see cref="_socket"/> until the connection closes or
    /// cancellation is requested. Each text frame is forwarded to
    /// <see cref="HandleMessage"/>.
    /// </summary>
    private async Task ReceiveLoopAsync() {
        var buffer = new byte[1024];
        while (_socket!.State == WebSocketState.Open && !_cts!.Token.IsCancellationRequested) {
            var result = await _socket.ReceiveAsync(buffer, _cts.Token);
            if (result.MessageType == WebSocketMessageType.Close) break;

            if (result is { MessageType: WebSocketMessageType.Text, Count: > 0 })
                HandleMessage(Encoding.UTF8.GetString(buffer, 0, result.Count));
        }
    }

    /// <summary>
    /// Disposes <see cref="_socket"/> and nulls the reference, then logs the
    /// disconnection. Called from the <c>finally</c> block of <see cref="RunAsync"/>.
    /// </summary>
    private void TearDownSocket() {
        _socket?.Dispose();
        _socket = null;
        Logger.Info("MmsWebSocketHandler: disconnected");
    }

    /// <summary>
    /// Extracts the <c>action</c> field from <paramref name="message"/> and
    /// routes it to the appropriate handler method.
    /// Unrecognised actions are silently ignored.
    /// </summary>
    /// <param name="message">Decoded UTF-8 text frame received from MMS.</param>
    private void HandleMessage(string message) {
        var span = message.AsSpan();
        var action = MmsJsonParser.ExtractValue(span, MmsFields.Action);

        switch (action) {
            case MmsActions.RefreshHostMapping: HandleRefreshHostMapping(span); break;
            case MmsActions.StartPunch: HandleStartPunch(span); break;
            case MmsActions.HostMappingReceived: HandleHostMappingReceived(); break;
            case MmsActions.JoinFailed: HandleJoinFailed(message); break;
        }
    }

    /// <summary>
    /// Handles a <c>refresh_host_mapping</c> message by extracting the join ID,
    /// discovery token, and server timestamp, then raising
    /// <see cref="RefreshHostMappingRequested"/>. Silently ignored if any required
    /// field is missing or unparseable.
    /// </summary>
    /// <param name="span">Span over the raw message text.</param>
    private void HandleRefreshHostMapping(ReadOnlySpan<char> span) {
        var joinId = MmsJsonParser.ExtractValue(span, MmsFields.JoinId);
        var token = MmsJsonParser.ExtractValue(span, MmsFields.HostDiscoveryToken);
        var timeStr = MmsJsonParser.ExtractValue(span, MmsFields.ServerTimeMs);

        if (joinId == null || token == null || !long.TryParse(timeStr, out var time))
            return;

        Logger.Info($"MmsWebSocketHandler: {MmsActions.RefreshHostMapping} for join {joinId}");
        RefreshHostMappingRequested?.Invoke(joinId, token, time);
    }

    /// <summary>
    /// Handles a <c>start_punch</c> message by extracting the join ID, client
    /// endpoint, host port, and start timestamp, then raising
    /// <see cref="StartPunchRequested"/>. Silently ignored if any required field
    /// is missing or unparseable.
    /// </summary>
    /// <param name="span">Span over the raw message text.</param>
    private void HandleStartPunch(ReadOnlySpan<char> span) {
        var joinId = MmsJsonParser.ExtractValue(span, MmsFields.JoinId);
        var clientIp = MmsJsonParser.ExtractValue(span, MmsFields.ClientIp);
        var clientPortStr = MmsJsonParser.ExtractValue(span, MmsFields.ClientPort);
        var hostPortStr = MmsJsonParser.ExtractValue(span, MmsFields.HostPort);
        var startTimeStr = MmsJsonParser.ExtractValue(span, MmsFields.StartTimeMs);

        if (joinId == null ||
            clientIp == null ||
            !int.TryParse(clientPortStr, out var clientPort) ||
            !int.TryParse(hostPortStr, out var hostPort) ||
            !long.TryParse(startTimeStr, out var startTimeMs))
            return;

        Logger.Info($"MmsWebSocketHandler: {MmsActions.StartPunch} for join {joinId} -> {clientIp}:{clientPort}");
        StartPunchRequested?.Invoke(joinId, clientIp, clientPort, hostPort, startTimeMs);
    }

    /// <summary>
    /// Handles a <c>host_mapping_received</c> message by logging and raising
    /// <see cref="HostMappingReceived"/>.
    /// </summary>
    private void HandleHostMappingReceived() {
        Logger.Info($"MmsWebSocketHandler: {MmsActions.HostMappingReceived}");
        HostMappingReceived?.Invoke();
    }

    /// <summary>
    /// Handles a <c>join_failed</c> message by logging the full message body
    /// as a warning. No event is raised; the host has no recovery action to take.
    /// </summary>
    /// <param name="message">Full raw message text, logged verbatim for diagnostics.</param>
    private static void HandleJoinFailed(string message) {
        Logger.Warn($"MmsWebSocketHandler: {MmsActions.JoinFailed} - {message}");
    }
}
