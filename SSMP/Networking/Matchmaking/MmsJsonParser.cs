using System;
using System.Buffers;

namespace SSMP.Networking.Matchmaking;

/// <summary>
/// Lightweight JSON helpers for reading and writing MMS API payloads.
/// These avoid a full JSON library dependency and are intentionally simple:
/// they assume well-formed server responses and handle only the small set
/// of value types (quoted strings, integers) that MMS actually returns.
/// </summary>
internal static class MmsJsonParser
{
    /// <summary>Shared pool for minimizing character buffer allocations.</summary>
    private static readonly ArrayPool<char> CharPool = ArrayPool<char>.Shared;
    
    /// <summary>
    /// Finds <c>"key":value</c> in <paramref name="json"/> and returns the value as a string.
    /// Supports both quoted string values and unquoted numeric values.
    /// Returns <c>null</c> when the key is absent.
    /// </summary>
    /// <param name="json">The JSON span to search.</param>
    /// <param name="key">The key to find.</param>
    /// <returns>The extracted value or <c>null</c>.</returns>
    public static string? ExtractValue(ReadOnlySpan<char> json, string key)
    {
        // "key":
        Span<char> searchKey = stackalloc char[key.Length + 3]; 
        searchKey[0] = '"';
        key.AsSpan().CopyTo(searchKey[1..]);
        searchKey[key.Length + 1] = '"';
        searchKey[key.Length + 2] = ':';

        var idx = json.IndexOf(searchKey, StringComparison.Ordinal);
        if (idx == -1) return null;

        var valueStart = idx + searchKey.Length;
        while (valueStart < json.Length && char.IsWhiteSpace(json[valueStart])) valueStart++;
        if (valueStart >= json.Length) return null;

        if (json[valueStart] == '"')
        {
            var valueEnd = json[(valueStart + 1)..].IndexOf('"');
            return valueEnd == -1 ? null : json.Slice(valueStart + 1, valueEnd).ToString();
        }

        // Numeric: read until non-numeric character
        var numericEnd = valueStart;
        while (numericEnd < json.Length &&
               (char.IsDigit(json[numericEnd]) || json[numericEnd] == '.' || json[numericEnd] == '-'))
        {
            numericEnd++;
        }
        return json.Slice(valueStart, numericEnd - valueStart).ToString();
    }

    /// <summary>
    /// Writes the CreateLobby JSON payload into a rented char buffer.
    /// Returns the number of characters written.
    /// The caller must return the buffer to <see cref="ArrayPool{T}.Shared"/> after use.
    /// </summary>
    /// <param name="port">The host port.</param>
    /// <param name="isPublic">Whether the lobby is public.</param>
    /// <param name="gameVersion">The game version string.</param>
    /// <param name="lobbyType">The type of the lobby.</param>
    /// <param name="hostLanIp">Optional local IP address.</param>
    /// <returns>A tuple containing the buffer and the number of characters written.</returns>
    public static (char[] buffer, int length) FormatCreateLobbyJson(
        int port,
        bool isPublic,
        string gameVersion,
        PublicLobbyType lobbyType,
        string? hostLanIp)
    {
        var lanIpPart = hostLanIp != null ? $",\"HostLanIp\":\"{hostLanIp}:{port}\"" : "";
        var matchmakingVersionPart = lobbyType == PublicLobbyType.Matchmaking
            ? $",\"MatchmakingVersion\":{MmsProtocol.CurrentVersion}"
            : "";

        var json =
            $"{{\"HostPort\":{port},\"IsPublic\":{BoolToJson(isPublic)},\"GameVersion\":\"{gameVersion}\"," +
            $"\"LobbyType\":\"{lobbyType.ToString().ToLower()}\"{lanIpPart}{matchmakingVersionPart}}}";

        var buffer = CharPool.Rent(json.Length);
        json.AsSpan().CopyTo(buffer);
        return (buffer, json.Length);
    }

    /// <summary>
    /// Returns a char buffer to the shared array pool.
    /// </summary>
    public static void ReturnBuffer(char[] buffer) => CharPool.Return(buffer);

    /// <summary>
    /// Returns the JSON representation of a boolean value.
    /// </summary>
    private static string BoolToJson(bool value) => value ? "true" : "false";
}
