using System.Net;
using System.Net.Sockets;
using System.Text;
using MMS.Services.Matchmaking;

namespace MMS.Services.Network;

/// <summary>
/// Hosted background service that listens for incoming UDP packets on a fixed port
/// as part of the NAT traversal discovery flow.
/// </summary>
/// <remarks>
/// Each valid packet carries a session token encoded as UTF-8. The sender's observed
/// external endpoint is recorded in <see cref="JoinSessionService"/>, which advances
/// the hole-punch state machine for the corresponding host or client session.
/// </remarks>
public sealed class UdpDiscoveryService : BackgroundService
{
    private readonly JoinSessionService _joinSessionService;
    private readonly ILogger<UdpDiscoveryService> _logger;

    /// <summary>The UDP port this service binds to at startup.</summary>
    private const int Port = 5001;

    /// <summary>
    /// The exact byte length a valid discovery packet must have.
    /// Packets of any other length are dropped before string decoding.
    /// </summary>
    private const int TokenByteLength = 32;

    /// <summary>
    /// Initialises a new instance of <see cref="UdpDiscoveryService"/>.
    /// </summary>
    /// <param name="joinSessionService">
    /// Receives the discovered port for each valid token, advancing the NAT hole-punch
    /// state machine for the corresponding host or client session.
    /// </param>
    /// <param name="logger">Logger for startup, shutdown, and per-packet diagnostics.</param>
    public UdpDiscoveryService(JoinSessionService joinSessionService, ILogger<UdpDiscoveryService> logger)
    {
        _joinSessionService = joinSessionService;
        _logger = logger;
    }

    /// <summary>
    /// Binds a <see cref="UdpClient"/> to <see cref="Port"/> and enters a receive loop
    /// until <paramref name="stoppingToken"/> is cancelled by the .NET hosting infrastructure
    /// (e.g. Ctrl+C, SIGTERM).
    /// </summary>
    /// <param name="stoppingToken">Cancellation token that signals application shutdown.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var udpClient = new UdpClient(Port);
        _logger.LogInformation("UDP Discovery Service listening on port {Port}", Port);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await udpClient.ReceiveAsync(stoppingToken);
                await ProcessPacketAsync(result.Buffer, result.RemoteEndPoint, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in UDP Discovery Service receive loop");
            }
        }

        _logger.LogInformation("UDP Discovery Service stopped");
    }

    /// <summary>
    /// Validates and processes a single UDP packet.
    /// </summary>
    /// <remarks>
    /// Byte-length validation is performed on <paramref name="buffer"/> before any string
    /// decoding to avoid a heap allocation for packets that would be rejected anyway
    /// (oversized probes, garbage data, etc.).
    /// </remarks>
    /// <param name="buffer">
    /// Raw bytes from the socket. Must be exactly <see cref="TokenByteLength"/> bytes.
    /// </param>
    /// <param name="remoteEndPoint">
    /// The NAT-translated public endpoint of the sender. The port component is stored
    /// as the discovered external port for the session.
    /// </param>
    /// <param name="cancellationToken">Token used to cancel downstream async operations.</param>
    private async Task ProcessPacketAsync(byte[] buffer, IPEndPoint remoteEndPoint, CancellationToken cancellationToken)
    {
        if (buffer.Length != TokenByteLength)
        {
            _logger.LogWarning(
                "Received malformed discovery packet from {EndPoint} (length: {Length})",
                FormatEndPoint(remoteEndPoint),
                buffer.Length
            );
            return;
        }

        var token = Encoding.UTF8.GetString(buffer);

        _logger.LogInformation(
            "Received discovery token {Token} from {EndPoint}",
            token,
            FormatEndPoint(remoteEndPoint)
        );

        await _joinSessionService.SetDiscoveredPortAsync(token, remoteEndPoint.Port, cancellationToken);
    }

    /// <summary>Formats an endpoint for logging, redacting it in non-development environments.</summary>
    private static string FormatEndPoint(IPEndPoint remoteEndPoint) =>
        Program.IsDevelopment ? remoteEndPoint.ToString() : "[Redacted]";
}
