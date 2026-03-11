using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;

namespace MMS.Services;

/// <summary>
/// Background service that listens on UDP port 5001 for endpoint discovery packets.
/// Accepts 17-byte packets: <c>[0x44 ('D')][16-byte Guid token]</c>.
/// <para>
/// For host tokens: records the external endpoint in <see cref="DiscoveryService"/>
/// so <c>POST /lobby</c> can use it.
/// For client tokens: records the endpoint and immediately pushes it to the host WebSocket.
/// </para>
/// </summary>
public sealed class UdpDiscoveryListener(
    DiscoveryService discoveryService,
    LobbyService lobbyService,
    ILogger<UdpDiscoveryListener> logger
) : BackgroundService {
    /// <summary>UDP port the listener binds to.</summary>
    private const int UdpPort = 5001;

    /// <summary>Maximum UDP packets accepted per source IP per <see cref="RateWindowTicks"/>.</summary>
    private const int MaxPacketsPerWindow = 20;

    /// <summary>Sliding window length for per-source rate limiting (1 second in stopwatch ticks).</summary>
    private static readonly long RateWindowTicks = Stopwatch.Frequency;

    /// <summary>Per-source-IP packet counters used for rate limiting.</summary>
    private readonly ConcurrentDictionary<IPAddress, (int Count, long WindowStart)> _rateLimits = new();

    /// <summary>Expected packet size: 1-byte opcode + 16-byte Guid.</summary>
    private const int PacketSize = 17;

    /// <summary>Opcode for endpoint discovery packets (<c>'D'</c>).</summary>
    private const byte DiscoveryOpcode = 0x44;

    /// <summary>
    /// Binds a UDP socket on <see cref="UdpPort"/> and dispatches received packets
    /// to <see cref="HandlePacketAsync"/> until <paramref name="stoppingToken"/> is signalled.
    /// Individual packet errors are logged and swallowed so a single bad packet cannot
    /// bring down the listener loop.
    /// </summary>
    /// <summary>
    /// Bind a UDP socket to the discovery port and process incoming discovery packets until cancellation.
    /// </summary>
    /// <param name="stoppingToken">Token that signals the service to stop when the host shuts down.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        using var udp = new UdpClient(UdpPort);
        logger.LogInformation("[UDP] Discovery listener started on port {Port}", UdpPort);

        while (!stoppingToken.IsCancellationRequested) {
            try {
                var result = await udp.ReceiveAsync(stoppingToken);
                await HandlePacketAsync(result, stoppingToken);
            } catch (OperationCanceledException) {
                break;
            } catch (Exception ex) {
                logger.LogError(ex, "[UDP] Error processing discovery packet");
            }
        }

        logger.LogInformation("[UDP] Discovery listener stopped");
    }

    /// <summary>
    /// Validates an incoming UDP packet, records its endpoint in <see cref="DiscoveryService"/>,
    /// and if the token belongs to a pending client join, pushes the endpoint to the host
    /// WebSocket via <see cref="PushClientEndpointAsync"/>.
    /// Packets with an unexpected length or opcode are silently discarded.
    /// </summary>
    /// <param name="result">The received UDP datagram and its remote endpoint.</param>
    /// <summary>
    /// Processes a received UDP discovery packet: validates the packet and records the mapping from discovery token to UDP endpoint; if a pending join exists for the token, resolves the host and sends the client's IP and UDP port to the host over its open WebSocket.
    /// </summary>
    /// <param name="result">The received UDP packet and the remote endpoint that sent it.</param>
    /// <param name="cancellationToken">Cancellation token propagated from ExecuteAsync.</param>
    private async Task HandlePacketAsync(UdpReceiveResult result, CancellationToken cancellationToken) {
        ReadOnlySpan<byte> data = result.Buffer;

        // Per-source-IP rate limit to prevent amplification attacks
        if (IsRateLimited(result.RemoteEndPoint.Address))
            return;

        // Validate packet length and opcode before any further processing
        if (data.Length != PacketSize || data[0] != DiscoveryOpcode)
            return;

        // Parse directly from the span to avoid a 16-byte heap allocation
        var token = new Guid(data.Slice(1, 16));
        var endpoint = result.RemoteEndPoint;

        discoveryService.Record(token, endpoint);
        logger.LogDebug("[UDP] Recorded endpoint {Endpoint} for token {Token}", endpoint, token);

        var pending = discoveryService.TryConsumePendingJoin(token);
        if (pending is null)
            return;

        var lobby = lobbyService.GetLobbyByToken(pending.Value.HostToken);
        if (lobby?.HostWebSocket is not { State: WebSocketState.Open } ws) {
            logger.LogWarning("[UDP] Host WebSocket unavailable for pending join token {Token}", token);
            return;
        }

        // Use the TCP-observed IP rather than the one in the packet to prevent MiTM spoofing.
        // The port still comes from UDP since that's what the client is actually hole-punching on.
        var safeEndpoint = new IPEndPoint(IPAddress.Parse(pending.Value.ClientIp), endpoint.Port);

        await PushClientEndpointAsync(ws, safeEndpoint, cancellationToken);
        logger.LogInformation("[UDP] Pushed client endpoint {Endpoint} to host via WebSocket", safeEndpoint);
    }

    /// <summary>
    /// Serialises the client endpoint as a JSON object and sends it as a WebSocket text frame.
    /// Uses a pooled byte buffer to avoid per-send heap allocations.
    /// </summary>
    /// <remarks>
    /// JSON format: <c>{"clientIp":"&lt;address&gt;","clientPort":&lt;port&gt;}</c>.
    /// The buffer is sized via <see cref="Encoding.GetMaxByteCount"/> and always returned
    /// to the pool in the <see langword="finally"/> block.
    /// </remarks>
    /// <param name="ws">The open WebSocket to send on.</param>
    /// <param name="endpoint">The client's external <see cref="IPEndPoint"/>.</param>
    /// <summary>
    /// Sends the client's IP address and port as a JSON text message over the given WebSocket.
    /// </summary>
    /// <param name="ws">The WebSocket to which the JSON message will be sent.</param>
    /// <param name="endpoint">The client's IP address and port to include in the message.</param>
    /// <param name="cancellationToken">Token to cancel the send operation.</param>
    /// <returns>A task that completes when the JSON message has been sent.</returns>
    private static async Task PushClientEndpointAsync(
        WebSocket ws,
        IPEndPoint endpoint,
        CancellationToken cancellationToken
    ) {
        var json = $"{{\"clientIp\":\"{endpoint.Address}\",\"clientPort\":{endpoint.Port}}}";


        var maxByteCount = Encoding.UTF8.GetMaxByteCount(json.Length);
        var rentedBuffer = ArrayPool<byte>.Shared.Rent(maxByteCount);
        try {
            var byteCount = Encoding.UTF8.GetBytes(json, rentedBuffer);
            var segment = new ArraySegment<byte>(rentedBuffer, 0, byteCount);
            await ws.SendAsync(segment, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        } finally {
            ArrayPool<byte>.Shared.Return(rentedBuffer);
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> if the given source address has exceeded
    /// <see cref="MaxPacketsPerWindow"/> packets within the current rate window,
    /// in which case the packet should be silently dropped.
    /// <summary>
    /// Checks whether the specified source IP has exceeded the configured packet rate within the current rate window.
    /// </summary>
    /// <param name="address">The source IP address to check.</param>
    /// <returns>`true` if the address has sent more than MaxPacketsPerWindow packets in the current rate window, `false` otherwise.</returns>
    /// <remarks>
    /// This method updates the internal per-address rate counter and window start time as part of its check.
    /// </remarks>
    private bool IsRateLimited(IPAddress address) {
        var now = Stopwatch.GetTimestamp();
        var entry = _rateLimits.GetOrAdd(address, _ => (0, now));

        // Reset counter when the window has elapsed
        if (now - entry.WindowStart > RateWindowTicks){
            entry = (1, now);
        }else{
            entry = (entry.Count + 1, entry.WindowStart);
        }
        _rateLimits[address] = entry;
        return entry.Count > MaxPacketsPerWindow;
    }
}
