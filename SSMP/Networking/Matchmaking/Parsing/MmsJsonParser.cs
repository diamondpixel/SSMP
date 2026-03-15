using System;
using System.Buffers;
using SSMP.Networking.Matchmaking.Protocol;
using SSMP.Networking.Matchmaking.Utilities;

namespace SSMP.Networking.Matchmaking.Parsing;

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
        // Build "key": search token on the stack
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

        return json[valueStart] == '"'
            ? ExtractStringValue(json, valueStart)
            : ExtractNumericValue(json, valueStart);
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
        var lanIpPart = hostLanIp != null
            ? $",\"{MmsFields.HostLanIpRequest}\":\"{hostLanIp}:{port}\""
            : "";

        var matchmakingVersionPart = lobbyType == PublicLobbyType.Matchmaking
            ? $",\"{MmsFields.MatchmakingVersionRequest}\":{MmsProtocol.CurrentVersion}"
            : "";

        var json =
            $"{{\"{MmsFields.HostPortRequest}\":{port},\"{MmsFields.IsPublicRequest}\":{MmsUtilities.BoolToJson(isPublic)}," +
            $"\"{MmsFields.GameVersionRequest}\":\"{gameVersion}\",\"{MmsFields.LobbyTypeRequest}\":\"{lobbyType.ToString().ToLower()}\"{lanIpPart}{matchmakingVersionPart}}}";

        var buffer = CharPool.Rent(json.Length);
        json.AsSpan().CopyTo(buffer);
        return (buffer, json.Length);
    }

    /// <summary>
    /// Returns a char buffer to the shared array pool.
    /// </summary>
    public static void ReturnBuffer(char[] buffer) => CharPool.Return(buffer);

    /// <summary>
    /// Extracts a quoted string value starting at the opening <c>"</c>.
    /// Returns <c>null</c> if the closing quote is missing.
    /// </summary>
    private static string? ExtractStringValue(ReadOnlySpan<char> json, int openQuoteIndex)
    {
        var valueEnd = json[(openQuoteIndex + 1)..].IndexOf('"');
        return valueEnd == -1 ? null : json.Slice(openQuoteIndex + 1, valueEnd).ToString();
    }

    /// <summary>
    /// Extracts an unquoted numeric value (digits, <c>.</c>, <c>-</c>) starting at
    /// <paramref name="start"/>. Returns an empty string if no numeric characters are found.
    /// </summary>
    private static string ExtractNumericValue(ReadOnlySpan<char> json, int start)
    {
        var end = start;
        while (end < json.Length &&
               (char.IsDigit(json[end]) || json[end] == '.' || json[end] == '-'))
        {
            end++;
        }
        return json.Slice(start, end - start).ToString();
    }
}
