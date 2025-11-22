using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using SSMP.Logging;
using SSMP.Util;

namespace SSMP.Networking.Server;

/// <summary>
/// DTLS implementation for a server-side peer for networking.
/// </summary>
internal class DtlsServer {
    public const int MaxPacketSize = 1400; // The maximum packet size for sending and receiving DTLS packets

    /// <summary>
    /// Connection states for tracking client lifecycle
    /// </summary>
    private enum ConnectionState {
        Handshaking,
        Connected,
        Disconnecting,
        Disconnected
    }

    /// <summary>
    /// Wrapper for connection state management
    /// </summary>
    private class ConnectionInfo {
        public ServerDatagramTransport DatagramTransport { get; set; }
        public ConnectionState State { get; set; }
        public long StateVersion { get; set; } // Increment on each state change
        public DtlsServerClient? Client { get; set; }
    }

    private DtlsServerProtocol? _serverProtocol;
    private ServerTlsServer? _tlsServer;

    private Socket?
        _socket; // The server only uses a single socket for all connections given that with UDP, we cannot create more than one on the same listening port

    private readonly ConcurrentDictionary<IPEndPoint, ConnectionInfo>
        _connections; // Dictionary mapping IP endpoints to connection info (includes pending handshakes and connected clients)

    private CancellationTokenSource? _cancellationTokenSource;
    private int _port;

    public event Action<DtlsServerClient, byte[], int>?
        DataReceivedEvent; // Event that is called when data is received from the given DTLS server client

    public DtlsServer() {
        _connections = new ConcurrentDictionary<IPEndPoint, ConnectionInfo>();
    }

    /// <summary>
    /// Start the DTLS server on the given port.
    /// </summary>
    public void Start(int port) {
        _port = port;
        _serverProtocol = new DtlsServerProtocol();
        _tlsServer = new ServerTlsServer(new BcTlsCrypto());
        _cancellationTokenSource = new CancellationTokenSource();
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(new IPEndPoint(IPAddress.Any, _port));
        new Thread(() => SocketReceiveLoop(_cancellationTokenSource.Token)) { IsBackground = true }.Start();
    }

    /// <summary>
    /// Stop the DTLS server by disconnecting all clients and cancelling all running threads.
    /// </summary>
    public void Stop() {
        ThreadUtil.Try(() => _cancellationTokenSource?.Cancel(), "DtlsServer.Stop - Cancel");
        Thread.Sleep(100); // Give threads a moment to exit gracefully
        ThreadUtil.Try(() => _tlsServer?.Cancel(), "DtlsServer.Stop - TlsServer");
        ThreadUtil.Try(() => _socket?.Close(), "DtlsServer.Stop - Socket");
        _socket = null;

        // Disconnect all clients
        foreach (var kvp in _connections) {
            var connInfo = kvp.Value;
            lock (connInfo) {
                if (connInfo.State == ConnectionState.Connected && connInfo.Client != null) {
                    InternalDisconnectClient(connInfo.Client);
                }

                ThreadUtil.Try(() => connInfo.DatagramTransport?.Close(), "DtlsServer.Stop - DatagramTransport");
                connInfo.State = ConnectionState.Disconnected;
            }
        }

        _connections.Clear();
        ThreadUtil.Try(() => _cancellationTokenSource?.Dispose(), "DtlsServer.Stop - Dispose");
        _cancellationTokenSource = null;
    }

    /// <summary>
    /// Disconnect the client with the given IP endpoint from the server.
    /// </summary>
    public void DisconnectClient(IPEndPoint endPoint) {
        if (!_connections.TryGetValue(endPoint, out var connInfo)) {
            Logger.Warn("Could not find connection to disconnect");
            return;
        }

        lock (connInfo) {
            if (connInfo.State != ConnectionState.Connected || connInfo.Client == null) {
                Logger.Warn($"Connection {endPoint} not in connected state");
                return;
            }

            connInfo.State = ConnectionState.Disconnecting;
            connInfo.StateVersion++;
            InternalDisconnectClient(connInfo.Client);
            connInfo.State = ConnectionState.Disconnected;
            connInfo.StateVersion++;
        }

        _connections.TryRemove(endPoint, out _);
    }

    /// <summary>
    /// Disconnects the client from the internal map immediately (allowing new connections),
    /// but keeps the transport alive for a specified delay to allow sending final packets.
    /// </summary>
    public void DisconnectClientDelayed(IPEndPoint endPoint, int delayMs) {
        if (!_connections.TryGetValue(endPoint, out var connInfo)) {
            Logger.Warn("Could not find connection to disconnect delayed");
            return;
        }

        DtlsServerClient? clientToDisconnect = null;
        lock (connInfo) {
            if (connInfo.State != ConnectionState.Connected || connInfo.Client == null) return;
            connInfo.State = ConnectionState.Disconnecting;
            connInfo.StateVersion++;
            clientToDisconnect = connInfo.Client;
        }

        _connections.TryRemove(endPoint, out _); // Remove from map immediately to allow reconnection

        if (clientToDisconnect != null) {
            Task.Run(async () => {
                await Task.Delay(delayMs);
                InternalDisconnectClient(clientToDisconnect);
            });
        }
    }

    /// <summary>
    /// Disconnect the given DTLS server client from the server. This will request cancellation of the "receive loop"
    /// for the client and close/cleanup the underlying DTLS objects.
    /// </summary>
    private void InternalDisconnectClient(DtlsServerClient dtlsServerClient) {
        ThreadUtil.Try(() => dtlsServerClient.ReceiveLoopTokenSource?.Cancel(), "DtlsServer.Disconnect - Cancel");
        ThreadUtil.Try(() => dtlsServerClient.DtlsTransport?.Close(), "DtlsServer.Disconnect - DtlsTransport");
        ThreadUtil.Try(() => dtlsServerClient.DatagramTransport?.Dispose(),
            "DtlsServer.Disconnect - DatagramTransport");
        ThreadUtil.Try(() => dtlsServerClient.ReceiveLoopTokenSource?.Dispose(), "DtlsServer.Disconnect - TokenSource");
    }

    /// <summary>
    /// Start a loop that will continuously receive data on the socket for existing and new clients.
    /// </summary>
    private void SocketReceiveLoop(CancellationToken cancellationToken) {
        var buffer = new byte[MaxPacketSize];

        while (!cancellationToken.IsCancellationRequested) {
            if (_socket == null) {
                Logger.Error("Socket was null during receive call");
                break;
            }

            EndPoint endPoint = new IPEndPoint(IPAddress.Any, 0);

            int numReceived;
            try {
                numReceived = _socket.ReceiveFrom(buffer, SocketFlags.None, ref endPoint);
            } catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset) {
                // This happens when a previous send failed (ICMP Port Unreachable), safe to ignore for UDP server
                continue;
            } catch (SocketException e) when (e.SocketErrorCode == SocketError.Interrupted) {
                break;
            } catch (ObjectDisposedException) {
                break;
            } catch (Exception e) {
                Logger.Error($"Unexpected exception in SocketReceiveLoop:\n{e}");
                continue;
            }

            if (numReceived < 0) break; // Error occurred
            if (numReceived == 0 || cancellationToken.IsCancellationRequested) continue;

            var ipEndPoint = (IPEndPoint) endPoint;

            // Create a copy of the buffer for this packet
            var packetBuffer = new byte[numReceived];
            Buffer.BlockCopy(buffer, 0, packetBuffer, 0, numReceived);

            ProcessReceivedPacket(ipEndPoint, packetBuffer, numReceived, cancellationToken);
        }

        Logger.Info("SocketReceiveLoop exited");
    }

    /// <summary>
    /// Process a received packet and route it to the appropriate connection or start a new handshake.
    /// </summary>
    private void ProcessReceivedPacket(IPEndPoint ipEndPoint, byte[] buffer, int numReceived,
        CancellationToken cancellationToken) {
        // Check if this looks like a ClientHello (new connection or reconnection)
        bool isClientHello = numReceived > 13 && buffer[0] == 22 && buffer[3] == 0 && buffer[4] == 0 && buffer[13] == 1;

        if (_connections.TryGetValue(ipEndPoint, out var connInfo)) {
            DtlsServerClient? clientToDisconnect = null;
            bool shouldRemove = false;

            lock (connInfo) {
                // If we receive ClientHello on an existing connection, treat as reconnection
                if (isClientHello && connInfo.State == ConnectionState.Connected) {
                    Logger.Info($"Received ClientHello from connected endpoint {ipEndPoint} - forcing reconnection");
                    clientToDisconnect = connInfo.Client;
                    connInfo.State = ConnectionState.Disconnected;
                    connInfo.StateVersion++;
                    connInfo.Client = null;
                    shouldRemove = true;
                } else if (connInfo.State == ConnectionState.Handshaking) {
                    // Still handshaking, route packet to existing transport
                    var added = ThreadUtil.Try(() => {
                        connInfo.DatagramTransport.ReceivedDataCollection.Add(new UdpDatagramTransport.ReceivedData {
                            Buffer = buffer,
                            Length = numReceived
                        }, cancellationToken);
                        return true;
                    }, null, false);

                    if (added) return;
                    shouldRemove = true;
                } else if (connInfo.State == ConnectionState.Connected) {
                    // Connected and not a ClientHello, route packet normally
                    ThreadUtil.Try(() => connInfo.DatagramTransport.ReceivedDataCollection.Add(
                        new UdpDatagramTransport.ReceivedData {
                            Buffer = buffer,
                            Length = numReceived
                        }, cancellationToken), null);
                    return;
                } else {
                    // Disconnecting or disconnected state, remove and allow new connection
                    shouldRemove = true;
                }
            }

            // Cleanup outside the lock to avoid blocking
            if (shouldRemove) {
                _connections.TryRemove(ipEndPoint, out _);
                if (clientToDisconnect != null) {
                    Task.Run(() =>
                        InternalDisconnectClient(
                            clientToDisconnect)); // Disconnect asynchronously to avoid blocking packet processing
                }
                // Fall through to create new connection
            } else {
                return;
            }
        }

        // New connection attempt - must be a valid ClientHello
        if (!isClientHello) return; // Ignore stray packets from unknown clients

        // Create new connection info
        var newTransport = new ServerDatagramTransport(_socket);
        newTransport.IPEndPoint = ipEndPoint;

        var newConnInfo = new ConnectionInfo {
            DatagramTransport = newTransport,
            State = ConnectionState.Handshaking,
            StateVersion = 0,
            Client = null
        };

        if (_connections.TryAdd(ipEndPoint, newConnInfo)) {
            // Add the initial packet to the transport
            var added = ThreadUtil.Try(() => {
                newTransport.ReceivedDataCollection.Add(new UdpDatagramTransport.ReceivedData {
                    Buffer = buffer,
                    Length = numReceived
                }, cancellationToken);
                return true;
            }, "DtlsServer.ProcessReceivedPacket - AddInitialPacket", false);

            if (!added) {
                _connections.TryRemove(ipEndPoint, out _);
                ThreadUtil.Try(() => newTransport.Dispose(), "DtlsServer.ProcessReceivedPacket - DisposeNewTransport");
                return;
            }

            // Spawn handshake task
            Task.Factory.StartNew(
                () => PerformHandshake(ipEndPoint, cancellationToken),
                cancellationToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default
            );
        } else {
            // Race condition - another thread added it between our remove and add
            ThreadUtil.Try(() => newTransport.Dispose(), null);

            // Try to add packet to existing connection
            if (_connections.TryGetValue(ipEndPoint, out var existingConnInfo)) {
                lock (existingConnInfo) {
                    if (existingConnInfo.State == ConnectionState.Handshaking) {
                        ThreadUtil.Try(() => existingConnInfo.DatagramTransport.ReceivedDataCollection.Add(
                            new UdpDatagramTransport.ReceivedData {
                                Buffer = buffer,
                                Length = numReceived
                            }, cancellationToken), null);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Performs the DTLS handshake for a new connection.
    /// </summary>
    private void PerformHandshake(IPEndPoint endPoint, CancellationToken cancellationToken) {
        if (_serverProtocol == null) {
            Logger.Error("Could not perform handshake because server protocol is null");
            CleanupFailedHandshake(endPoint);
            return;
        }

        if (!_connections.TryGetValue(endPoint, out var connInfo)) {
            Logger.Error($"Connection info not found for {endPoint}");
            return;
        }

        Logger.Info($"Starting handshake for {endPoint}");

        DtlsTransport? dtlsTransport = null;
        bool handshakeSucceeded = false;

        try {
            // Perform handshake with timeout using Task
            var handshakeTask = Task.Run(() => ThreadUtil.Try(
                () => _serverProtocol.Accept(_tlsServer, connInfo.DatagramTransport),
                "DtlsServer.PerformHandshake - Accept",
                null as DtlsTransport
            ), cancellationToken);

            if (handshakeTask.Wait(10000)) {
                dtlsTransport = handshakeTask.Result;
                handshakeSucceeded = dtlsTransport != null;
                if (handshakeSucceeded) Logger.Info($"Handshake successful for {endPoint}");
                else Logger.Warn($"Handshake failed for {endPoint}");
            } else {
                Logger.Warn($"Handshake timed out for {endPoint}");
            }
        } catch (TlsFatalAlert e) {
            Logger.Warn($"TLS Fatal Alert during handshake with {endPoint}: {e.AlertDescription}");
        } catch (AggregateException ae) when (ae.InnerException is TlsFatalAlert tfa) {
            Logger.Warn($"TLS Fatal Alert during handshake with {endPoint}: {tfa.AlertDescription}");
        } catch (OperationCanceledException) {
            // Cancellation is expected
        } catch (Exception e) {
            Logger.Error($"Exception during handshake with {endPoint}: {e.Message}");
        }

        if (!handshakeSucceeded || dtlsTransport == null || cancellationToken.IsCancellationRequested) {
            CleanupFailedHandshake(endPoint);
            return;
        }

        // Transition to connected state
        lock (connInfo) {
            if (connInfo.State != ConnectionState.Handshaking) {
                Logger.Warn($"Connection {endPoint} no longer in handshaking state");
                ThreadUtil.Try(() => dtlsTransport.Close(), "DtlsServer.PerformHandshake - CloseDtlsTransport");
                CleanupFailedHandshake(endPoint);
                return;
            }

            var dtlsServerClient = new DtlsServerClient {
                DtlsTransport = dtlsTransport,
                DatagramTransport = connInfo.DatagramTransport,
                EndPoint = endPoint,
                ReceiveLoopTokenSource = new CancellationTokenSource()
            };

            connInfo.Client = dtlsServerClient;
            connInfo.State = ConnectionState.Connected;
            connInfo.StateVersion++;

            new Thread(() => ClientReceiveLoop(dtlsServerClient, dtlsServerClient.ReceiveLoopTokenSource.Token))
                { IsBackground = true }.Start();
        }
    }

    /// <summary>
    /// Clean up a failed handshake attempt.
    /// </summary>
    private void CleanupFailedHandshake(IPEndPoint endPoint) {
        if (_connections.TryRemove(endPoint, out var connInfo)) {
            ThreadUtil.Try(() => connInfo.DatagramTransport?.Close(), "DtlsServer.CleanupFailedHandshake");
        }
    }

    /// <summary>
    /// Start a loop for the given DTLS server client that will continuously check whether new data is available
    /// on the DTLS transport for that client. Will evoke the DataReceivedEvent in case data is received for that client.
    /// </summary>
    private void ClientReceiveLoop(DtlsServerClient dtlsServerClient, CancellationToken cancellationToken) {
        var dtlsTransport = dtlsServerClient.DtlsTransport;

        while (!cancellationToken.IsCancellationRequested) {
            var buffer = new byte[dtlsTransport.GetReceiveLimit()];

            var numReceived = ThreadUtil.Try(
                () => dtlsTransport.Receive(buffer, 0, buffer.Length,
                    100), // Increased timeout to 100ms to reduce CPU usage
                null,
                -1
            );
            if (numReceived <= 0) continue;
            ThreadUtil.Try(() => DataReceivedEvent?.Invoke(dtlsServerClient, buffer, numReceived),
                "DtlsServer.DataReceivedEvent");
        }
    }
}
