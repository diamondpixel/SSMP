using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using SSMP.Logging;
using SSMP.Util;

namespace SSMP.Networking.Client;

/// <summary>
/// DTLS implementation for a client-side peer for networking.
/// </summary>
internal class DtlsClient {
    public const int MaxPacketSize = 1400; // The maximum packet size for sending and receiving DTLS packets

    public const int
        DtlsHandshakeTimeoutMillis =
            10000; // The maximum time the DTLS handshake can take in milliseconds before timing out (increased from 5000)

    private const int SioUDPConnReset = -1744830452; // IO Control Code for Connection Reset on socket (0x9800000C)

    private Socket? _socket;
    private ClientTlsClient? _tlsClient;
    private ClientDatagramTransport? _clientDatagramTransport;
    private CancellationTokenSource? _receiveTaskTokenSource;
    private readonly object _connectionLock = new object();

    public DtlsTransport?
        DtlsTransport { get; private set; } // DTLS transport instance from establishing a connection to a server

    public event Action<byte[], int>? DataReceivedEvent; // Event that is called when data is received from the server

    /// <summary>
    /// Try to establish a connection to a server with the given address and port.
    /// </summary>
    /// <exception cref="SocketException">Thrown when the underlying socket fails to connect to the server.</exception>
    /// <exception cref="IOException">Thrown when the DTLS protocol fails to connect to the server.</exception>
    public void Connect(string address, int port) {
        lock (_connectionLock) {
            // Clean up any existing connection first
            if (_socket != null || _tlsClient != null || _clientDatagramTransport != null || DtlsTransport != null ||
                _receiveTaskTokenSource != null) {
                InternalDisconnect();
                Thread.Sleep(100); // Give threads time to exit
            }

            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            //_socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveTimeout, 1000); 
            //_socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.SendTimeout, 1000);

            // Prevent UDP WSAECONNRESET (10054) from surfacing as exceptions on Windows when the remote endpoint closes
            ThreadUtil.Try(() => _socket.IOControl((IOControlCode) SioUDPConnReset, [0, 0, 0, 0], null),
                "DtlsClient.Connect - IOControl");

            try {
                _socket.Connect(address, port);
            } catch (SocketException e) {
                Logger.Error($"Socket exception when connecting UDP socket:\n{e}");
                InternalDisconnect();
                throw;
            }

            var clientProtocol = new DtlsClientProtocol();
            _tlsClient = new ClientTlsClient(new BcTlsCrypto());
            _clientDatagramTransport = new ClientDatagramTransport(_socket);
            _receiveTaskTokenSource =
                new CancellationTokenSource(); // Create the token source, because we need the token for the receive loop
            var cancellationToken = _receiveTaskTokenSource.Token;

            new Thread(() => SocketReceiveLoop(cancellationToken))
                    { IsBackground = true }
                .Start(); // Start the socket receive loop, since during the DTLS connection, it needs to receive data

            // Perform handshake with timeout
            DtlsTransport? dtlsTransport = null;
            bool handshakeSucceeded = false;

            try {
                var handshakeTask = Task.Run(() => ThreadUtil.Try(
                    () => clientProtocol.Connect(_tlsClient, _clientDatagramTransport),
                    "DtlsClient.Connect - Handshake",
                    null as DtlsTransport
                ), cancellationToken);

                if (handshakeTask.Wait(DtlsHandshakeTimeoutMillis)) {
                    dtlsTransport = handshakeTask.Result;
                    handshakeSucceeded = dtlsTransport != null;
                } else {
                    Logger.Error($"DTLS handshake timed out after {DtlsHandshakeTimeoutMillis}ms");
                    _receiveTaskTokenSource?.Cancel(); // Cancel the receive loop
                    throw new TlsTimeoutException("DTLS handshake timed out");
                }
            } catch (TlsTimeoutException) {
                InternalDisconnect();
                throw;
            } catch (AggregateException ae) when (ae.InnerException is TlsTimeoutException) {
                InternalDisconnect();
                throw ae.InnerException;
            } catch (AggregateException ae) when (ae.InnerException is IOException) {
                Logger.Error($"IO exception when connecting DTLS client:\n{ae.InnerException}");
                InternalDisconnect();
                throw ae.InnerException;
            } catch (OperationCanceledException) {
                InternalDisconnect();
                throw new IOException("Connection cancelled");
            } catch (Exception e) {
                Logger.Error($"Unexpected exception during DTLS handshake:\n{e}");
                InternalDisconnect();
                throw;
            }

            if (!handshakeSucceeded || dtlsTransport == null) {
                InternalDisconnect();
                throw new IOException("Failed to establish DTLS connection");
            }

            DtlsTransport = dtlsTransport;
            new Thread(() => DtlsReceiveLoop(cancellationToken))
                { IsBackground = true }.Start(); // Start DTLS receive loop
        }
    }

    /// <summary>
    /// Disconnect the DTLS client from the server. This will cancel, dispose, or close all internal objects to
    /// clean up potential previous connection attempts.
    /// </summary>
    public void Disconnect() {
        lock (_connectionLock) {
            InternalDisconnect();
        }
    }

    /// <summary>
    /// Internal disconnect implementation without locking (assumes caller holds lock or is called from Connect).
    /// </summary>
    private void InternalDisconnect() {
        ThreadUtil.Try(() => _receiveTaskTokenSource?.Cancel(), "DtlsClient.Disconnect - Cancel");
        Thread.Sleep(50); // Give threads a moment to exit
        ThreadUtil.Try(() => DtlsTransport?.Close(), "DtlsClient.Disconnect - DtlsTransport");
        DtlsTransport = null;
        ThreadUtil.Try(() => _clientDatagramTransport?.Dispose(), "DtlsClient.Disconnect - Transport");
        _clientDatagramTransport = null;
        ThreadUtil.Try(() => _tlsClient?.Cancel(), "DtlsClient.Disconnect - TlsClient");
        _tlsClient = null;
        ThreadUtil.Try(() => _socket?.Close(), "DtlsClient.Disconnect - Socket");
        _socket = null;
        ThreadUtil.Try(() => _receiveTaskTokenSource?.Dispose(), "DtlsClient.Disconnect - TokenSource");
        _receiveTaskTokenSource = null;
    }

    /// <summary>
    /// Continuously tries to receive data from the socket until cancellation is requested.
    /// </summary>
    private void SocketReceiveLoop(CancellationToken cancellationToken) {
        while (!cancellationToken.IsCancellationRequested) {
            if (_socket == null) break;

            EndPoint endPoint = new IPEndPoint(IPAddress.Any, 0);
            var buffer = new byte[MaxPacketSize];

            int numReceived;
            try {
                numReceived = _socket.ReceiveFrom(buffer, SocketFlags.None, ref endPoint);
            } catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut) {
                continue;
            } catch (SocketException e) when (e.SocketErrorCode == SocketError.Interrupted) {
                break;
            } catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset) {
                break;
            } catch (ObjectDisposedException) {
                break;
            } catch (Exception e) {
                Logger.Error($"Unexpected exception in SocketReceiveLoop:\n{e}");
                continue;
            }

            if (_clientDatagramTransport == null) break;

            // CRITICAL FIX: Create a copy of the buffer for this specific packet. The original buffer will be reused in the next iteration
            var packetBuffer = new byte[numReceived];
            Buffer.BlockCopy(buffer, 0, packetBuffer, 0, numReceived);

            bool added = false;
            try {
                _clientDatagramTransport.ReceivedDataCollection.Add(new UdpDatagramTransport.ReceivedData {
                    Buffer = packetBuffer, // Use the copy, not the original buffer
                    Length = numReceived
                }, cancellationToken);
                added = true;
            } catch (OperationCanceledException) {
                // Expected during disconnect
            } catch (InvalidOperationException) {
                // Collection might be marked as complete for adding
            } catch (Exception e) {
                Logger.Error($"Error adding data to transport collection:\n{e}");
            }

            if (!added) break; // Collection disposed, completed, or cancelled
        }
    }

    /// <summary>
    /// Continuously tries to receive data from the DTLS transport until cancellation is requested.
    /// </summary>
    private void DtlsReceiveLoop(CancellationToken cancellationToken) {
        while (!cancellationToken.IsCancellationRequested && DtlsTransport != null) {
            var buffer = new byte[MaxPacketSize];
            var length = ThreadUtil.Try(
                () => DtlsTransport.Receive(buffer, 0, buffer.Length,
                    100), // Increased timeout from 5ms to 100ms to reduce CPU usage
                null, // Don't log timeouts/normal exceptions
                -1
            );

            if (length > 0)
                ThreadUtil.Try(() => DataReceivedEvent?.Invoke(buffer, length), "DtlsClient.DataReceivedEvent");
        }
    }
}
