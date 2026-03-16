using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SSMP.Logging;

namespace SSMP.Networking.Matchmaking.Utilities;

/// <summary>
/// General-purpose utility helpers shared across MMS components.
/// All methods are stateless and free of side-effects.
/// </summary>
internal static class MmsUtilities
{
    /// <summary>
    /// Converts an HTTP or HTTPS URL to its WebSocket equivalent.
    /// <c>http://</c> -> <c>ws://</c> and <c>https://</c> -> <c>wss://</c>.
    /// </summary>
    public static string ToWebSocketUrl(string httpUrl) =>
        httpUrl.Replace("http://", "ws://").Replace("https://", "wss://");

    /// <summary>
    /// Returns the JSON literal for a boolean value: <c>"true"</c> or <c>"false"</c>.
    /// </summary>
    public static string BoolToJson(bool value) => value ? "true" : "false";

    /// <summary>
    /// Observes a fire-and-forget task and logs unexpected failures.
    /// </summary>
    /// <param name="task">The task to monitor.</param>
    /// <param name="owner">Component name included in failure logs.</param>
    /// <param name="operationName">Human-readable operation label for diagnostics.</param>
    public static void RunBackground(Task task, string owner, string operationName) =>
        _ = ObserveAsync(task, owner, operationName);

    /// <summary>
    /// Reads one complete text message from a <see cref="ClientWebSocket"/>, assembling fragmented frames.
    /// </summary>
    /// <param name="socket">The connected client WebSocket to read from.</param>
    /// <param name="cancellationToken">Cancellation token for the receive loop.</param>
    /// <param name="maxMessageBytes">Maximum allowed payload size before the read fails.</param>
    /// <returns>
    /// A tuple containing the terminal frame type and the decoded text payload.
    /// Non-text messages and close frames return <see langword="null"/> as the payload.
    /// </returns>
    public static async Task<(WebSocketMessageType messageType, string? message)> ReceiveTextMessageAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken,
        int maxMessageBytes = 16 * 1024
    )
    {
        const int chunkSize = 1024;

        var buffer = new byte[chunkSize];
        var writer = new ArrayBufferWriter<byte>();

        while (true)
        {
            var frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (frame.MessageType == WebSocketMessageType.Close)
                return (frame.MessageType, null);

            AppendFrame(writer, buffer, frame.Count, maxMessageBytes);

            if (!frame.EndOfMessage)
                continue;

            return frame.MessageType != WebSocketMessageType.Text
                ? (frame.MessageType, null)
                : (frame.MessageType, writer.WrittenCount == 0 ? string.Empty : Encoding.UTF8.GetString(writer.WrittenSpan));
        }
    }

    /// <summary>
    /// Determines the local machine's outbound IPv4 address by connecting a
    /// disposable UDP socket to a known external address. Does not transmit any data.
    /// </summary>
    /// <returns>
    /// The local IP address as a string, or <c>null</c> if the address could not
    /// be determined (e.g. no network interface available).
    /// </returns>
    public static string? GetLocalIpAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Awaits a background task and suppresses expected cancellation while logging unexpected failures.
    /// </summary>
    /// <param name="task">The task being observed.</param>
    /// <param name="owner">Component name included in failure logs.</param>
    /// <param name="operationName">Human-readable operation label for diagnostics.</param>
    private static async Task ObserveAsync(Task task, string owner, string operationName)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { /*ignored*/ }
        catch (Exception ex) { Logger.Warn($"{owner}: {operationName} failed: {ex.Message}"); }
    }

    /// <summary>
    /// Appends a single received frame into the accumulating message buffer while enforcing the message size cap.
    /// </summary>
    /// <param name="writer">The destination buffer holding the message assembled so far.</param>
    /// <param name="buffer">Scratch receive buffer containing the latest frame bytes.</param>
    /// <param name="count">Number of valid bytes currently in <paramref name="buffer"/>.</param>
    /// <param name="maxMessageBytes">Maximum total message size allowed.</param>
    private static void AppendFrame(ArrayBufferWriter<byte> writer, byte[] buffer, int count, int maxMessageBytes)
    {
        if (count <= 0)
            return;

        var nextLength = writer.WrittenCount + count;
        if (nextLength > maxMessageBytes)
            throw new InvalidOperationException("Matchmaking WebSocket message exceeded the maximum size.");

        buffer.AsSpan(0, count).CopyTo(writer.GetSpan(count));
        writer.Advance(count);
    }
}
