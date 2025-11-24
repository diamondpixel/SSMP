using System;
using System.Collections.Concurrent;
using System.Net;
using SSMP.Networking.Server;
using SSMP.Networking.Transport.Common;

namespace SSMP.Networking.Transport.UDP;

/// <summary>
/// UDP server with DTLS encryption.
/// Wraps DtlsServer to implement IEncryptedTransportServer.
/// </summary>
internal class UdpEncryptedTransportServer : IEncryptedTransportServer {
    private readonly DtlsServer _dtlsServer;
    private readonly ConcurrentDictionary<string, UdpEncryptedTransportClient> _clients;
    
    public event Action<IEncryptedTransportClient>? ClientConnectedEvent;
    
    public UdpEncryptedTransportServer() {
        _dtlsServer = new DtlsServer();
        _clients = new ConcurrentDictionary<string, UdpEncryptedTransportClient>();
        
        // When DTLS server gets data, check if it's a new connection
        _dtlsServer.DataReceivedEvent += OnDtlsData;
    }
    
    public void Start(int port) {
        _dtlsServer.Start(port);
    }
    
    public void Stop() {
        _dtlsServer.Stop();
        _clients.Clear();
    }
    
    public void DisconnectClient(IEncryptedTransportClient client) {
        if (client is UdpEncryptedTransportClient udpClient) {
            _dtlsServer.DisconnectClient(udpClient.EndPoint);
            _clients.TryRemove(client.ClientIdentifier, out _);
        }
    }
    
    private void OnDtlsData(DtlsServerClient dtlsClient, byte[] buffer, int length) {
        var identifier = dtlsClient.EndPoint.ToString();
        
        // Check if this is a new client
        if (!_clients.TryGetValue(identifier, out var client)) {
            client = new UdpEncryptedTransportClient(dtlsClient);
            if (_clients.TryAdd(identifier, client)) {
                // New client connected
                ClientConnectedEvent?.Invoke(client);
            }
        }
        
        // Forward data to client wrapper
        client.OnDataReceived(buffer, length);
    }
}

/// <summary>
/// Wrapper for a single UDP+DTLS client on the server.
/// </summary>
internal class UdpEncryptedTransportClient : IEncryptedTransportClient {
    private readonly DtlsServerClient _dtlsClient;
    
    public string ClientIdentifier => _dtlsClient.EndPoint.ToString();
    public IPEndPoint EndPoint => _dtlsClient.EndPoint;
    
    public event Action<byte[], int>? DataReceivedEvent;
    
    public UdpEncryptedTransportClient(DtlsServerClient dtlsClient) {
        _dtlsClient = dtlsClient;
    }
    
    public int Send(byte[] buffer, int offset, int length) {
        if (_dtlsClient.DtlsTransport == null) return 0;
        _dtlsClient.DtlsTransport.Send(buffer, offset, length);
        return length;
    }
    
    internal void OnDataReceived(byte[] buffer, int length) {
        DataReceivedEvent?.Invoke(buffer, length);
    }
}
