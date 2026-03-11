using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace MMS.Models;

/// <summary>
/// Client waiting for NAT hole-punch.
/// </summary>
public record PendingClient(string ClientIp, int ClientPort, DateTime RequestedAt);

/// <summary>
/// Game lobby. ConnectionData serves as both identifier and connection info.
/// Steam: ConnectionData = Steam lobby ID. Matchmaking: ConnectionData = IP:Port.
/// </summary>
public class Lobby(
    string connectionData,
    string hostToken,
    string lobbyCode,
    string lobbyName,
    string lobbyType = "matchmaking",
    string? hostLanIp = null,
    bool isPublic = true
) {
    /// <summary>Connection data: Steam lobby ID for Steam, IP:Port for matchmaking.</summary>
    public string ConnectionData { get; } = connectionData;

    /// <summary>Secret token for host authentication.</summary>
    public string HostToken { get; } = hostToken;

    /// <summary>Human-readable 6-character invite code.</summary>
    public string LobbyCode { get; } = lobbyCode;

    /// <summary>Display name of the lobby.</summary>
    public string LobbyName { get; } = lobbyName;

    /// <summary>Lobby type: "steam" or "matchmaking".</summary>
    public string LobbyType { get; } = lobbyType;

    /// <summary>Optional LAN IP for local network discovery.</summary>
    public string? HostLanIp { get; } = hostLanIp;

    /// <summary>Whether the lobby should appear in public browser listings.</summary>
    public bool IsPublic { get; } = isPublic;

    /// <summary>Timestamp of the last heartbeat from the host.</summary>
    public DateTime LastHeartbeat { get; set; } = DateTime.UtcNow;

    /// <summary>Maximum number of pending clients before the queue stops accepting new entries.</summary>
    private const int MaxPendingClients = 64;

    /// <summary>Queue of clients waiting for NAT hole-punch.</summary>
    private readonly Queue<PendingClient> _pendingClients = new();

    /// <summary>Synchronization primitive for queue operations.</summary>
    private readonly object _syncRoot = new();

    /// <summary>
    /// Attempts to enqueue a pending client atomically. Returns <see langword="false"/> if the queue
    /// has reached <see cref="MaxPendingClients"/>, preventing unbounded memory growth.
    /// </summary>
    public bool TryEnqueuePendingClient(PendingClient client) {
        lock (_syncRoot) {
            if (_pendingClients.Count >= MaxPendingClients) return false;
            _pendingClients.Enqueue(client);
            return true;
        }
    }

    /// <summary>
    /// Attempts to dequeue a pending client atomically. Returns <see langword="true"/>
    /// if a client was successfully dequeued; otherwise <see langword="false"/>.
    /// </summary>
    public bool TryDequeuePendingClient(out PendingClient client) {
        lock (_syncRoot) {
            return _pendingClients.TryDequeue(out client);
        }
    }

    /// <summary>True if no heartbeat received in the last 60 seconds.</summary>
    public bool IsDead => DateTime.UtcNow - LastHeartbeat > TimeSpan.FromSeconds(60);

    /// <summary>
    /// WebSocket connection from the host for push notifications.
    /// Marked <see langword="volatile"/> so the UDP background service always reads the
    /// latest value written by the HTTP thread without requiring a lock.
    /// </summary>
    private volatile WebSocket? _hostWebSocket;

    /// <inheritdoc cref="_hostWebSocket"/>
    public WebSocket? HostWebSocket {
        get => _hostWebSocket;
        set => _hostWebSocket = value;
    }
}
