using System;

namespace SSMP.Networking.Transport.Common;

/// <summary>
/// Server-side encrypted transport.
/// </summary>
internal interface IEncryptedTransportServer {
    /// <summary>
    /// Event fired when a client completes encrypted handshake/connection.
    /// Parameter: IEncryptedTransportClient
    /// </summary>
    event Action<IEncryptedTransportClient>? ClientConnectedEvent;
    
    /// <summary>
    /// Start listening on port/channel.
    /// </summary>
    void Start(int port);
    
    /// <summary>
    /// Stop server and disconnect all clients.
    /// </summary>
    void Stop();
    
    /// <summary>
    /// Disconnect specific client.
    /// </summary>
    void DisconnectClient(IEncryptedTransportClient client);
}

/// <summary>
/// Represents a connected client from server's perspective.
/// </summary>
internal interface IEncryptedTransportClient {
    /// <summary>
    /// Unique client identifier (IPEndPoint.ToString() or SteamID.ToString()).
    /// </summary>
    string ClientIdentifier { get; }
    
    /// <summary>
    /// Event fired when data received from this client.
    /// </summary>
    event Action<byte[], int>? DataReceivedEvent;
    
    /// <summary>
    /// Send encrypted data to this client.
    /// </summary>
    int Send(byte[] buffer, int offset, int length);
}
