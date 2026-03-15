using System.Net;
using System.Net.Sockets;

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
}
