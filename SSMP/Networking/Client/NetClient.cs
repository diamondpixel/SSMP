using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using Org.BouncyCastle.Tls;
using SSMP.Api.Client;
using SSMP.Api.Client.Networking;
using SSMP.Logging;
using SSMP.Networking.Chunk;
using SSMP.Networking.Packet;
using SSMP.Networking.Packet.Data;
using SSMP.Networking.Packet.Update;
using SSMP.Util;

namespace SSMP.Networking.Client;

/// <summary>
/// The networking client that manages the UDP client for sending and receiving data. This only
/// manages client side networking, e.g. sending to and receiving from the server.
/// </summary>
internal class NetClient : INetClient {
    private readonly PacketManager _packetManager;
    private readonly DtlsClient _dtlsClient;
    private readonly ClientChunkSender _chunkSender;
    private readonly ClientChunkReceiver _chunkReceiver;
    private readonly ClientConnectionManager _connectionManager;
    private readonly object _connectionLock = new object();
    private byte[]? _leftoverData;
    private volatile bool _isConnecting = false;

    public ClientUpdateManager UpdateManager { get; }
    public event Action<ServerInfo>? ConnectEvent;
    public event Action<ConnectionFailedResult>? ConnectFailedEvent;
    public event Action? DisconnectEvent;
    public event Action? TimeoutEvent;
    public ClientConnectionStatus ConnectionStatus { get; private set; } = ClientConnectionStatus.NotConnected;
    public bool IsConnected => ConnectionStatus == ClientConnectionStatus.Connected;

    /// <summary>
    /// Construct the net client with the given packet manager.
    /// </summary>
    public NetClient(PacketManager packetManager) {
        _packetManager = packetManager;
        _dtlsClient = new DtlsClient();
        UpdateManager = new ClientUpdateManager();
        _chunkSender = new ClientChunkSender(UpdateManager);
        _chunkReceiver = new ClientChunkReceiver(UpdateManager);
        _connectionManager = new ClientConnectionManager(_packetManager, _chunkSender, _chunkReceiver);
        _dtlsClient.DataReceivedEvent += OnReceiveData;
        _connectionManager.ServerInfoReceivedEvent += OnServerInfoReceived;
    }

    /// <summary>
    /// Starts establishing a connection with the given host on the given port.
    /// </summary>
    public void Connect(string address, int port, string username, string authKey, List<AddonData> addonData) {
        // Prevent multiple simultaneous connection attempts
        lock (_connectionLock) {
            if (_isConnecting) {
                Logger.Warn("Connection attempt already in progress, ignoring duplicate request");
                return;
            }

            if (ConnectionStatus == ClientConnectionStatus.Connected) {
                Logger.Warn("Already connected, disconnecting first");
                InternalDisconnect();
            }

            _isConnecting = true;
            ConnectionStatus = ClientConnectionStatus.Connecting;
        }

        // Logger.Debug($"Trying to connect NetClient to '{address}:{port}'");

        // Use Task instead of Thread for better resource management
        Task.Run(() => {
            try {
                _dtlsClient.Connect(address, port);
            } catch (TlsTimeoutException) {
                Logger.Info("DTLS connection timed out");
                HandleConnectFailed(new ConnectionFailedResult { Reason = ConnectionFailedReason.TimedOut });
                return;
            } catch (SocketException e) {
                Logger.Error($"Failed to connect due to SocketException:\n{e}");
                HandleConnectFailed(new ConnectionFailedResult { Reason = ConnectionFailedReason.SocketException });
                return;
            } catch (IOException e) {
                Logger.Error($"Failed to connect due to IOException:\n{e}");
                HandleConnectFailed(new ConnectionFailedResult { Reason = ConnectionFailedReason.IOException });
                return;
            } catch (Exception e) {
                Logger.Error($"Unexpected exception during connection:\n{e}");
                HandleConnectFailed(new ConnectionFailedResult { Reason = ConnectionFailedReason.IOException });
                return;
            }

            UpdateManager.DtlsTransport = _dtlsClient.DtlsTransport;
            // During the connection process we register the connection failed callback if we time out
            UpdateManager.TimeoutEvent += OnConnectTimedOut;
            UpdateManager.StartUpdates();

            // Logger.Debug("Starting connection with connection manager");
            _chunkSender.Start();
            _connectionManager.StartConnection(username, authKey, addonData);

            lock (_connectionLock) {
                _isConnecting = false;
            }
        });
    }

    /// <summary>
    /// Disconnect from the current server.
    /// </summary>
    public void Disconnect() {
        lock (_connectionLock) {
            InternalDisconnect();
        }
    }

    /// <summary>
    /// Internal disconnect implementation without locking (assumes caller holds lock).
    /// </summary>
    private void InternalDisconnect() {
        if (ConnectionStatus == ClientConnectionStatus.NotConnected && !_isConnecting) return;

        ThreadUtil.Try(() => {
            UpdateManager.StopUpdates();
            UpdateManager.TimeoutEvent -= OnConnectTimedOut;
            UpdateManager.TimeoutEvent -= OnUpdateTimedOut;
            _chunkSender.Stop();
            _chunkReceiver.Reset();
            _dtlsClient.Disconnect();
        }, "NetClient.InternalDisconnect");

        ConnectionStatus = ClientConnectionStatus.NotConnected;
        _isConnecting = false;
        _packetManager
            .ClearClientAddonUpdatePacketHandlers(); // Clear all client addon packet handlers, because their IDs become invalid
        _leftoverData = null; // Clear leftover data
        ThreadUtil.InvokeSafe(DisconnectEvent, "NetClient.DisconnectEvent"); // Invoke callback if it exists
    }

    /// <summary>
    /// Callback method for when the DTLS client receives data. This will update the update manager that we have
    /// received data, handle packet creation from raw data, handle login responses, and forward received packets to
    /// the packet manager.
    /// </summary>
    private void OnReceiveData(byte[] buffer, int length) {
        // Early exit if not connected
        if (ConnectionStatus == ClientConnectionStatus.NotConnected) {
            // Logger.Debug("Client is not connected to a server, but received data, ignoring");
            return;
        }

        var packets = ThreadUtil.Try(
            () => PacketManager.HandleReceivedData(buffer, length, ref _leftoverData),
            "NetClient.OnReceiveData - HandleReceivedData",
            new List<Packet.Packet>()
        );

        if (packets.Count == 0) return;

        foreach (var packet in packets) {
            // Create a ClientUpdatePacket from the raw packet instance, and read the values into it
            var clientUpdatePacket = new ClientUpdatePacket();
            if (!clientUpdatePacket.ReadPacket(packet)) {
                // Logger.Debug(
                //     "Received malformed packet, ignoring"); // If ReadPacket returns false, we received a malformed packet, which we simply ignore for now
                continue;
            }

            UpdateManager.OnReceivePacket<ClientUpdatePacket, ClientUpdatePacketId>(clientUpdatePacket);

            // First check for slice or slice ack data and handle it separately by passing it onto either the chunk sender or chunk receiver
            var packetData = clientUpdatePacket.GetPacketData();

            // Handle Slice data
            if (packetData.TryGetValue(ClientUpdatePacketId.Slice, out var sliceData)) {
                packetData.Remove(ClientUpdatePacketId.Slice);
                ThreadUtil.Try(() => _chunkReceiver.ProcessReceivedData((SliceData) sliceData),
                    "NetClient.OnReceiveData - ProcessSliceData");
            }

            // Handle SliceAck data
            if (packetData.TryGetValue(ClientUpdatePacketId.SliceAck, out var sliceAckData)) {
                packetData.Remove(ClientUpdatePacketId.SliceAck);
                ThreadUtil.Try(() => _chunkSender.ProcessReceivedData((SliceAckData) sliceAckData),
                    "NetClient.OnReceiveData - ProcessSliceAckData");
            }

            // Then, if we are already connected to a server, we let the packet manager handle the rest of the packet data (only if there's remaining data to process)
            if (ConnectionStatus == ClientConnectionStatus.Connected && packetData.Count > 0) {
                ThreadUtil.Try(() => _packetManager.HandleClientUpdatePacket(clientUpdatePacket),
                    "NetClient.OnReceiveData - HandleClientUpdatePacket");
            }
        }
    }

    /// <summary>
    /// Callback method for when server info is received during connection.
    /// </summary>
    private void OnServerInfoReceived(ServerInfo serverInfo) {
        if (serverInfo.ConnectionResult == ServerConnectionResult.Accepted) {
            // Logger.Debug("Connection to server accepted");

            // De-register the "connect failed" and register the actual timeout handler if we time out
            UpdateManager.TimeoutEvent -= OnConnectTimedOut;
            UpdateManager.TimeoutEvent += OnUpdateTimedOut;

            lock (_connectionLock) {
                ConnectionStatus = ClientConnectionStatus.Connected;
                _isConnecting = false;
            }

            // Invoke callback if it exists on the main thread of Unity
            ThreadUtil.RunSafeOnMainThread(
                () => ThreadUtil.InvokeSafe(ConnectEvent, serverInfo, "NetClient.ConnectEvent"),
                "NetClient.OnServerInfoReceived");
            return;
        }

        if (serverInfo.ConnectionResult != ServerConnectionResult.Accepted) {
            var reason = serverInfo.ConnectionResult == ServerConnectionResult.InvalidAddons
                ? ConnectionFailedReason.InvalidAddons
                : ConnectionFailedReason.Other;

            ConnectionFailedResult result;

            if (reason == ConnectionFailedReason.InvalidAddons) {
                result = new ConnectionInvalidAddonsResult {
                    Reason = reason,
                    AddonData = serverInfo.AddonData
                };
            } else {
                result = new ConnectionFailedMessageResult {
                    Reason = reason,
                    Message = serverInfo.ConnectionRejectedMessage ?? "Unknown reason"
                };
            }

            HandleConnectFailed(result);
            return;
        }
    }

    /// <summary>
    /// Callback method for when the client connection fails.
    /// </summary>
    private void OnConnectTimedOut() => HandleConnectFailed(new ConnectionFailedResult
        { Reason = ConnectionFailedReason.TimedOut });

    /// <summary>
    /// Callback method for when the client times out while connected.
    /// </summary>
    private void OnUpdateTimedOut() {
        ThreadUtil.RunSafeOnMainThread(() => ThreadUtil.InvokeSafe(TimeoutEvent, "NetClient.TimeoutEvent"),
            "NetClient.OnUpdateTimedOut");
    }

    /// <summary>
    /// Handles a failed connection with the given result.
    /// </summary>
    private void HandleConnectFailed(ConnectionFailedResult result) {
        lock (_connectionLock) {
            InternalDisconnect();
        }

        ThreadUtil.RunSafeOnMainThread(
            () => ThreadUtil.InvokeSafe(ConnectFailedEvent, result, "NetClient.ConnectFailedEvent"),
            "NetClient.HandleConnectFailed");
    }

    /// <inheritdoc />
    public IClientAddonNetworkSender<TPacketId> GetNetworkSender<TPacketId>(ClientAddon addon) where TPacketId : Enum {
        if (addon == null) throw new ArgumentException("Parameter 'addon' cannot be null");

        // Check whether this addon has actually requested network access through their property
        // We check this otherwise an ID has not been assigned and it can't send network data
        if (!addon.NeedsNetwork)
            throw new InvalidOperationException("Addon has not requested network access through property");

        // Check whether there already is a network sender for the given addon
        if (addon.NetworkSender != null) {
            if (!(addon.NetworkSender is IClientAddonNetworkSender<TPacketId> addonNetworkSender)) {
                throw new InvalidOperationException("Cannot request network senders with differing generic parameters");
            }

            return addonNetworkSender;
        }

        // Otherwise create one, store it and return it
        var newAddonNetworkSender = new ClientAddonNetworkSender<TPacketId>(this, addon);
        addon.NetworkSender = newAddonNetworkSender;
        return newAddonNetworkSender;
    }

    /// <inheritdoc />
    public IClientAddonNetworkReceiver<TPacketId> GetNetworkReceiver<TPacketId>(ClientAddon addon,
        Func<TPacketId, IPacketData> packetInstantiator) where TPacketId : Enum {
        if (addon == null) throw new ArgumentException("Parameter 'addon' cannot be null");
        if (packetInstantiator == null) throw new ArgumentException("Parameter 'packetInstantiator' cannot be null");

        // Check whether this addon has actually requested network access through their property
        // We check this otherwise an ID has not been assigned and it can't send network data
        if (!addon.NeedsNetwork)
            throw new InvalidOperationException("Addon has not requested network access through property");

        ClientAddonNetworkReceiver<TPacketId>? networkReceiver = null;

        // Check whether an existing network receiver exists
        if (addon.NetworkReceiver == null) {
            networkReceiver = new ClientAddonNetworkReceiver<TPacketId>(addon, _packetManager);
            addon.NetworkReceiver = networkReceiver;
        } else if (addon.NetworkReceiver is not IClientAddonNetworkReceiver<TPacketId>) {
            throw new InvalidOperationException("Cannot request network receivers with differing generic parameters");
        }

        networkReceiver?.AssignAddonPacketInfo(packetInstantiator);
        return (addon.NetworkReceiver as IClientAddonNetworkReceiver<TPacketId>)!;
    }
}
