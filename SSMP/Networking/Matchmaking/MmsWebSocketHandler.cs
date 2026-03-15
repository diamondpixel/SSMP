using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SSMP.Logging;

namespace SSMP.Networking.Matchmaking;

/// <summary>
/// Manages the persistent WebSocket connection between a lobby host and MMS.
/// MMS pushes control events over this channel to coordinate matchmaking flow.
/// </summary>
internal sealed class MmsWebSocketHandler : IDisposable
{
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
    /// Raised when MMS confirms the host mapping has been learned and refresh packets can stop.
    /// </summary>
    public event Action? HostMappingReceived;

    public MmsWebSocketHandler(string wsBaseUrl)
    {
        _wsBaseUrl = wsBaseUrl;
    }

    /// <summary>
    /// Opens the WebSocket connection and begins listening for host push events.
    /// Runs on a background thread; returns immediately.
    /// </summary>
    public void Start(string hostToken)
    {
        Stop();
        Task.Run(() => RunAsync(hostToken));
    }

    /// <summary>Closes the WebSocket connection.</summary>
    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _socket?.Dispose();
        _socket = null;
    }

    public void Dispose() => Stop();

    /// <summary>
    /// The main background loop for receiving and handling WebSocket messages.
    /// </summary>
    /// <param name="hostToken">The host token for authentication.</param>
    private async Task RunAsync(string hostToken)
    {
        _cts = new CancellationTokenSource();
        _socket = new ClientWebSocket();

        try
        {
            await _socket.ConnectAsync(new Uri($"{_wsBaseUrl}/ws/{hostToken}"), _cts.Token);
            Logger.Info("MmsWebSocketHandler: connected");

            var buffer = new byte[1024];
            while (_socket.State == WebSocketState.Open && !_cts.Token.IsCancellationRequested)
            {
                var result = await _socket.ReceiveAsync(buffer, _cts.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;

                if (result is { MessageType: WebSocketMessageType.Text, Count: > 0 })
                    HandleMessage(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Error($"MmsWebSocketHandler: error - {ex.Message}");
        }
        finally
        {
            _socket?.Dispose();
            _socket = null;
            Logger.Info("MmsWebSocketHandler: disconnected");
        }
    }

    /// <summary>
    /// Parses and dispatches messages received from MMS via WebSocket.
    /// </summary>
    /// <param name="message">The raw message string to handle.</param>
    private void HandleMessage(string message)
    {
        var span = message.AsSpan();
        var action = MmsJsonParser.ExtractValue(span, "action");

        switch (action)
        {
            case "refresh_host_mapping":
                var joinId = MmsJsonParser.ExtractValue(span, "joinId");
                var token = MmsJsonParser.ExtractValue(span, "hostDiscoveryToken");
                var timeStr = MmsJsonParser.ExtractValue(span, "serverTimeMs");
                if (joinId != null && token != null && long.TryParse(timeStr, out var time))
                {
                    Logger.Info($"MmsWebSocketHandler: refresh_host_mapping for join {joinId}");
                    RefreshHostMappingRequested?.Invoke(joinId, token, time);
                }
                break;

            case "start_punch":
                var punchJoinId = MmsJsonParser.ExtractValue(span, "joinId");
                var clientIp = MmsJsonParser.ExtractValue(span, "clientIp");
                var clientPortStr = MmsJsonParser.ExtractValue(span, "clientPort");
                var hostPortStr = MmsJsonParser.ExtractValue(span, "hostPort");
                var startTimeStr = MmsJsonParser.ExtractValue(span, "startTimeMs");
                if (punchJoinId != null &&
                    clientIp != null &&
                    int.TryParse(clientPortStr, out var clientPort) &&
                    int.TryParse(hostPortStr, out var hostPort) &&
                    long.TryParse(startTimeStr, out var startTimeMs))
                {
                    Logger.Info($"MmsWebSocketHandler: start_punch for join {punchJoinId} -> {clientIp}:{clientPort}");
                    StartPunchRequested?.Invoke(punchJoinId, clientIp, clientPort, hostPort, startTimeMs);
                }
                break;

            case "host_mapping_received":
                Logger.Info("MmsWebSocketHandler: host_mapping_received");
                HostMappingReceived?.Invoke();
                break;

            case "join_failed":
                Logger.Warn($"MmsWebSocketHandler: join_failed - {message}");
                break;
        }
    }
}
