using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using SSMP.Networking.Matchmaking.Parsing;
using SSMP.Networking.Matchmaking.Protocol;

namespace SSMP.Networking.Matchmaking.Transport;

/// <summary>
/// Thin HTTP transport layer for MMS API calls.
/// Owns a single shared <see cref="HttpClient"/> instance for connection-pool reuse
/// and surfaces typed success/error results to callers.
/// </summary>
internal sealed class MmsHttpClient
{
    /// <summary>Shared HTTP client instance for connection pooling.</summary>
    private static readonly HttpClient Http = CreateHttpClient();

    static MmsHttpClient()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Http.Dispose();
    }

    /// <summary>Latest matchmaking error from the most recent HTTP call.</summary>
    public MatchmakingError LastError { get; private set; } = MatchmakingError.None;

    /// <summary>Resets the last error state.</summary>
    public void ClearError() => LastError = MatchmakingError.None;

    /// <summary>
    /// Performs a GET request to the specified URL.
    /// </summary>
    public async Task<(bool success, string? body)> GetAsync(string url)
    {
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            var body = await response.Content.ReadAsStringAsync();
            InspectErrorBody(response.StatusCode, body);
            return (response.IsSuccessStatusCode, body);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            LastError = MatchmakingError.NetworkFailure;
            return (false, null);
        }
    }

    /// <summary>
    /// Performs a POST request with a JSON body to the specified URL.
    /// </summary>
    public async Task<(bool success, string? body)> PostJsonAsync(string url, string json)
    {
        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync(url, content);
            var body = await response.Content.ReadAsStringAsync();
            InspectErrorBody(response.StatusCode, body);
            return (response.IsSuccessStatusCode, body);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            LastError = MatchmakingError.NetworkFailure;
            return (false, null);
        }
    }

    /// <summary>Fire-and-forget DELETE, failures are silently swallowed by the caller.</summary>
    public static Task DeleteAsync(string url) => Http.DeleteAsync(url);

    /// <summary>
    /// Checks the response body for MMS-specific error codes.
    /// </summary>
    /// <param name="status">The HTTP status code.</param>
    /// <param name="body">The response body.</param>
    private void InspectErrorBody(HttpStatusCode status, string? body)
    {
        if ((int)status < 400 || body == null) return;

        var errorCode = MmsJsonParser.ExtractValue(body.AsSpan(), MmsFields.ErrorCode);
        LastError = errorCode == MmsProtocol.UpdateRequiredErrorCode
            ? MatchmakingError.UpdateRequired
            : MatchmakingError.NetworkFailure;
    }

    /// <summary>
    /// Determines if an exception represents a transient network issue.
    /// </summary>
    /// <param name="ex">The exception to check.</param>
    /// <returns><c>true</c> if transient; otherwise, <c>false</c>.</returns>
    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException;

    /// <summary>
    /// Configures and returns an optimized <see cref="HttpClient"/> instance.
    /// </summary>
    /// <returns>A new <see cref="HttpClient"/> instance.</returns>
    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false
        };

        ServicePointManager.DefaultConnectionLimit = 10;
        ServicePointManager.UseNagleAlgorithm = false;
        ServicePointManager.Expect100Continue = false;

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMilliseconds(MmsProtocol.HttpTimeoutMs)
        };
    }
}
