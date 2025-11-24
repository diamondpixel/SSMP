using System;

namespace SSMP.Networking.Transport.Common;

/// <summary>
/// Encrypted transport connection interface.
/// Implementations handle both connection establishment and encrypted data transfer.
/// </summary>
internal interface IEncryptedTransport {
    /// <summary>
    /// Event fired when encrypted data is received from remote peer.
    /// Parameters: (buffer, length)
    /// </summary>
    event Action<byte[], int>? DataReceivedEvent;
    
    /// <summary>
    /// Connect to remote peer.
    /// </summary>
    /// <param name="address">Connection address (format depends on implementation)</param>
    /// <param name="port">Port number or channel</param>
    void Connect(string address, int port);
    
    /// <summary>
    /// Send encrypted data.
    /// </summary>
    /// <returns>Number of bytes sent</returns>
    int Send(byte[] buffer, int offset, int length);
    
    /// <summary>
    /// Receive encrypted data (blocking with timeout).
    /// </summary>
    /// <param name="waitMillis">Milliseconds to wait (0 = no wait)</param>
    /// <returns>Number of bytes received</returns>
    int Receive(byte[] buffer, int offset, int length, int waitMillis);
    
    /// <summary>
    /// Disconnect and cleanup.
    /// </summary>
    void Disconnect();
}
