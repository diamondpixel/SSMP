using System.Collections.Concurrent;
using System.Net;

namespace MMS.Services;

/// <summary>
/// Stores UDP-discovered external endpoints and pending client join tokens.
/// </summary>
public sealed class DiscoveryService {
    /// <summary>How long a recorded endpoint or pending join remains valid.</summary>
    private static readonly TimeSpan EntryLifetime = TimeSpan.FromSeconds(60);

    /// <summary>How often <see cref="WaitForDiscoveryAsync"/> polls the cache.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    // Stores discovered endpoints. Expiry is a monotonic timestamp (ticks).
    private readonly ConcurrentDictionary<Guid, (IPEndPoint Endpoint, long ExpiryTicks)> _cache = new();

    // clientIp comes from the TCP layer so it cannot be spoofed via the plaintext UDP packet.
    private readonly ConcurrentDictionary<Guid, (string HostToken, string ClientIp, long RegisteredTicks)> _pendingJoins = new();

    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initialises a new <see cref="DiscoveryService"/>.
    /// </summary>
    /// <param name="timeProvider">
    /// Abstraction over time. Pass <see cref="TimeProvider.System"/> in production
    /// or a fake in tests. Defaults to <see cref="TimeProvider.System"/> if omitted.
    /// <summary>
    /// Initializes a new instance of <see cref="DiscoveryService"/> and configures the time provider used for expiry and timing operations.
    /// </summary>
    /// <param name="timeProvider">Optional <see cref="TimeProvider"/> for monotonic timestamps; if null, <see cref="TimeProvider.System"/> is used.</param>
    public DiscoveryService(TimeProvider? timeProvider = null) {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Records a discovered external endpoint for the given token.
    /// Expires after <see cref="EntryLifetime"/>.
    /// <summary>
    /// Records the given UDP endpoint for the specified token and sets its expiry to EntryLifetime.
    /// </summary>
    /// <param name="token">Token that identifies the recorded endpoint.</param>
    /// <param name="endpoint">Remote IP endpoint observed via UDP to associate with the token.</param>
    public void Record(Guid token, IPEndPoint endpoint) {
        var expiryTicks = GetExpiryTicks(EntryLifetime);
        _cache[token] = (endpoint, expiryTicks);
    }

    /// <summary>
    /// Returns the recorded endpoint for a token, or <see langword="null"/> if not found or expired.
    /// Expired entries are evicted on access.
    /// <summary>
    /// Retrieve the recorded UDP endpoint for the specified discovery token if it exists and has not expired.
    /// </summary>
    /// <param name="token">The discovery token associated with the endpoint.</param>
    /// <returns>The recorded <see cref="IPEndPoint"/> if present and not expired, or <c>null</c> otherwise.</returns>
    private IPEndPoint? TryGet(Guid token) {
        if (!_cache.TryGetValue(token, out var entry))
            return null;

        if (!IsExpired(entry.ExpiryTicks))
            return entry.Endpoint;

        _cache.TryRemove(token, out _);
        return null;
    }

    /// <summary>
    /// Polls for a discovered endpoint until it appears or the timeout elapses.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="PeriodicTimer"/> rather than <c>Task.Delay</c> in a loop since
    /// it does not re-queue the timer on each tick and has clean cancellation semantics.
    /// The cache is checked once immediately before the first timer tick to avoid a
    /// <see cref="PollInterval"/> delay when the packet has already arrived.
    /// </remarks>
    /// <param name="token">Token to wait for.</param>
    /// <param name="timeout">Maximum time to wait before returning <see langword="null"/>.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>
    /// The discovered <see cref="IPEndPoint"/> on success;
    /// <see langword="null"/> if the timeout elapses or <paramref name="ct"/> is cancelled.
    /// <summary>
    —Waits for a UDP-discovered endpoint associated with the specified token until one is found or the timeout elapses.
    </summary>
    /// <param name="token">The discovery token identifying the endpoint to wait for.</param>
    /// <param name="timeout">Maximum duration to wait for a discovery result.</param>
    /// <param name="ct">Token to cancel the wait operation.</param>
    /// <returns>The discovered <see cref="IPEndPoint"/> for the token, or <c>null</c> if none is found before the timeout or cancellation.</returns>
    public async Task<IPEndPoint?> WaitForDiscoveryAsync(
        Guid token,
        TimeSpan timeout,
        CancellationToken ct = default
    ) {
        var ep = TryGet(token);
        if (ep is not null)
            return ep;

        using var timer = new PeriodicTimer(PollInterval);
        var deadlineTicks = GetExpiryTicks(timeout);

        while (!IsExpired(deadlineTicks) && await timer.WaitForNextTickAsync(ct)) {
            ep = TryGet(token);
            if (ep is not null)
                return ep;
        }

        return null;
    }

    /// <summary>
    /// Registers a client token waiting for UDP discovery.
    /// Maps <paramref name="clientToken"/> to <paramref name="hostToken"/> so the UDP
    /// listener can find and notify the correct lobby host.
    /// </summary>
    /// <param name="clientToken">Token the client will embed in its UDP discovery packet.</param>
    /// <param name="hostToken">Host token of the lobby the client is joining.</param>
    /// <param name="clientIp">
    /// The client's IP address as seen by the TCP layer, immune to UDP-level spoofing.
    /// Used instead of the UDP packet's source address when pushing the client endpoint to the host WebSocket.
    /// <summary>
    /// Registers a client join token and associates it with the host token and the client's observed IP address for later consumption.
    /// The registration timestamp is recorded and used to expire the entry after the service's entry lifetime.
    /// </summary>
    /// <param name="clientToken">The client token to register.</param>
    /// <param name="hostToken">The host token associated with the client.</param>
    /// <param name="clientIp">The client's observed IP address as seen by the server.</param>
    public void RegisterPendingJoin(Guid clientToken, string hostToken, string clientIp) {
        _pendingJoins[clientToken] = (hostToken, clientIp, _timeProvider.GetTimestamp());
    }

    /// <summary>
    /// Atomically removes and returns the host token and TCP-observed client IP associated
    /// with a pending join. Returns <see langword="null"/> if the token is not registered.
    /// <summary>
            /// Atomically removes and returns the host token and client IP associated with a pending client join token.
            /// </summary>
            /// <param name="clientToken">The client join token to consume (previously registered via RegisterPendingJoin).</param>
            /// <returns>The tuple `(HostToken, ClientIp)` for the consumed pending join, or `null` if the token was not registered.</returns>
    public (string HostToken, string ClientIp)? TryConsumePendingJoin(Guid clientToken)
        => _pendingJoins.TryRemove(clientToken, out var entry)
            ? (entry.HostToken, entry.ClientIp)
            : null;

    /// <summary>
    /// Removes expired endpoint cache entries and stale pending joins.
    /// Called periodically by <see cref="LobbyCleanupService"/>.
    /// <summary>
    /// Removes expired UDP-discovered endpoints and stale pending join entries from internal stores.
    /// </summary>
    /// <remarks>
    /// Evicts cache entries whose expiry timestamp has passed and removes pending join records older than the configured entry lifetime using the service's time provider.
    /// </remarks>
    internal void Cleanup() {
        var now = _timeProvider.GetTimestamp();

        foreach (var (key, value) in _cache) {
            if (now > value.ExpiryTicks)
                _cache.TryRemove(key, out _);
        }

        var pendingJoinCutoff = now - DurationToTicks(EntryLifetime);
        foreach (var (key, value) in _pendingJoins) {
            if (value.RegisteredTicks < pendingJoinCutoff)
                _pendingJoins.TryRemove(key, out _);
        }
    }

    /// <summary>
        /// Convert a <see cref="TimeSpan"/> duration into provider timestamp ticks using the time provider's frequency.
        /// </summary>
        /// <param name="duration">The duration to convert.</param>
        /// <returns>The equivalent duration expressed in the time provider's timestamp ticks.</returns>
    private long DurationToTicks(TimeSpan duration)
        => (long)(_timeProvider.TimestampFrequency * duration.TotalSeconds);

    /// <summary>
        /// Get a monotonic timestamp representing the expiry time after the specified lifetime.
        /// </summary>
        /// <param name="lifetime">The duration from now until the expiry timestamp.</param>
        /// <returns>A monotonic timestamp (in timestamp ticks) equal to the current timestamp plus <paramref name="lifetime"/>.</returns>
    private long GetExpiryTicks(TimeSpan lifetime)
        => _timeProvider.GetTimestamp() + DurationToTicks(lifetime);

    /// <summary>
        /// Determines whether the provided expiry timestamp has passed relative to the current monotonic timestamp.
        /// </summary>
        /// <param name="expiryTicks">Expiry timestamp in the time provider's tick units.</param>
        /// <returns><c>true</c> if the current timestamp is greater than <paramref name="expiryTicks"/>, <c>false</c> otherwise.</returns>
    private bool IsExpired(long expiryTicks)
        => _timeProvider.GetTimestamp() > expiryTicks;
}
