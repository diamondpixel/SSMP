namespace SSMP.Networking.Packet.Data;

/// <summary>
/// Packet data for a chat message.
/// </summary>
internal class ChatMessage : IPacketData {
    /// <summary>
    /// The maximum length of a chat message.
    /// </summary>
    public const byte MaxMessageLength = byte.MaxValue;

    /// <inheritdoc />
    public bool IsReliable => true;

    /// <inheritdoc />
    public bool DropReliableDataIfNewerExists => false;

    /// <summary>
    /// The message string.
    /// </summary>
    public string Message { get; set; } = null!;

    /// <summary>
    /// The target player ID for private messages. Null for public messages.
    /// </summary>
    public ushort? TargetId { get; set; }

    /// <inheritdoc />
    public void WriteData(IPacket packet) {
        packet.Write(Message);
        packet.Write(TargetId.HasValue);
        if (TargetId.HasValue) {
            packet.Write(TargetId.Value);
        }
    }

    /// <inheritdoc />
    public void ReadData(IPacket packet) {
        Message = packet.ReadString();
        var hasTarget = packet.ReadBool();
        if (hasTarget) {
            TargetId = packet.ReadUShort();
        }
    }
}
