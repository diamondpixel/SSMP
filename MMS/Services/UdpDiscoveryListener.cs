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
    /// <param name="stoppingToken">Cancellation token that stops the service when the host shuts down.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        using var udp = new UdpClient(AddressFamily.InterNetworkV6);
        udp.Client.DualMode = true;
        udp.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, UdpPort));

        logger.LogInformation("[UDP] Discovery listener started on port {Port} (Dual-Mode)", UdpPort);

        _ = Task.Run(async () => {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
            while (await timer.WaitForNextTickAsync(stoppingToken)) {
                var now = Stopwatch.GetTimestamp();
                var evicted = _rateLimits
                              .Where(kvp => now - kvp.Value.WindowStart > RateWindowTicks * 2)
                              .Count(kvp => _rateLimits.TryRemove(kvp.Key, out _));

                if (evicted > 0)
                    logger.LogDebug("[UDP] Evicted {Count} stale rate limit entries", evicted);
            }
        }, stoppingToken);

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
    /// <param name="cancellationToken">Propagated from <see cref="ExecuteAsync"/>.</param>
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

        var pending = discoveryService.TryGetPendingJoin(token);
        if (pending is null)
            return;

        var lobby = lobbyService.GetLobbyByToken(pending.Value.HostToken);
        if (lobby?.HostWebSocket is not { State: WebSocketState.Open } ws) {
            logger.LogWarning("[UDP] Host WebSocket unavailable for pending join token {Token}", token);
            return;
        }

        // Only consume the join after we confirm the WebSocket is open
        discoveryService.RemovePendingJoin(token);

        // Use the TCP-observed IP rather than the one in the packet to prevent MiTM spoofing.
        // The port still comes from UDP since that's what the client is actually hole-punching on.
        if (!IPAddress.TryParse(pending.Value.ClientIp, out var clientIp)) {
            logger.LogWarning("[UDP] Invalid pending client IP {Ip} for token {Token}", pending.Value.ClientIp, token);
            return;
        }
        var safeEndpoint = new IPEndPoint(clientIp, endpoint.Port);

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
    /// <param name="cancellationToken">Propagated from <see cref="ExecuteAsync"/>.</param>
    private static async Task PushClientEndpointAsync(
        WebSocket ws,
        IPEndPoint endpoint,
        CancellationToken cancellationToken
    ) {
        var payload = new { clientIp = endpoint.Address.ToString(), clientPort = endpoint.Port };
        var json = System.Text.Json.JsonSerializer.Serialize(payload);

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
    /// </summary>
    private bool IsRateLimited(IPAddress address) {
        var now = Stopwatch.GetTimestamp();
        var entry = _rateLimits.AddOrUpdate(
            address,
            _ => (1, now),
            (_, existing) => now - existing.WindowStart > RateWindowTicks
                ? (1, now)
                : (existing.Count + 1, existing.WindowStart)
        );

        return entry.Count > MaxPacketsPerWindow;
    }
}
