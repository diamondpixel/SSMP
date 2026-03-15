using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.HttpOverrides;
using MMS.Models;
using MMS.Services.Lobby;
using MMS.Services.Matchmaking;
using MMS.Services.Network;

namespace MMS;

/// <summary>
/// Entry point and endpoint registration for the MatchMaking Server.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class Program {
    /// <summary>Whether the application is running in a development environment.</summary>
    internal static bool IsDevelopment { get; private set; }

    /// <summary>Application-level logger, set after the host is built.</summary>
    private static ILogger Logger { get; set; } = null!;

    /// <summary>Entry point.</summary>
    public static void Main(string[] args) {
        var builder = WebApplication.CreateBuilder(args);

        IsDevelopment = builder.Environment.IsDevelopment();

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options => {
                options.SingleLine = true;
                options.IncludeScopes = false;
                options.TimestampFormat = "HH:mm:ss ";
            }
        );

        builder.Services.AddSingleton<LobbyNameService>();
        builder.Services.AddSingleton<LobbyService>();
        builder.Services.AddSingleton<JoinSessionService>();
        builder.Services.AddHostedService<LobbyCleanupService>();
        builder.Services.AddHostedService<UdpDiscoveryService>();

        builder.Services.Configure<ForwardedHeadersOptions>(options => {
                options.ForwardedHeaders =
                    ForwardedHeaders.XForwardedFor |
                    ForwardedHeaders.XForwardedHost |
                    ForwardedHeaders.XForwardedProto;
            }
        );

        if (IsDevelopment) {
            builder.Services.AddHttpLogging(_ => { });
        } else {
            if (!ConfigureHttpsCertificate(builder))
                return;
        }

        builder.Services.AddRateLimiter(options => {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                options.OnRejected = async (context, token) => {
                    // The current client treats 429s as generic failures.
                    // Keep this response stable until the client adds explicit rate-limit handling.
                    await context.HttpContext.Response.WriteAsJsonAsync(
                        new ErrorResponse("Too many requests. Please try again later."), cancellationToken: token
                    );
                };

                options.AddPolicy(
                    "create", context =>
                        RateLimitPartition.GetFixedWindowLimiter(
                            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                            factory: _ => new FixedWindowRateLimiterOptions {
                                PermitLimit = 5,
                                Window = TimeSpan.FromSeconds(30),
                                QueueLimit = 0
                            }
                        )
                );

                options.AddPolicy(
                    "search", context =>
                        RateLimitPartition.GetFixedWindowLimiter(
                            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                            factory: _ => new FixedWindowRateLimiterOptions {
                                PermitLimit = 10,
                                Window = TimeSpan.FromSeconds(10),
                                QueueLimit = 0
                            }
                        )
                );

                options.AddPolicy(
                    "join", context =>
                        RateLimitPartition.GetFixedWindowLimiter(
                            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                            factory: _ => new FixedWindowRateLimiterOptions {
                                PermitLimit = 5,
                                Window = TimeSpan.FromSeconds(30),
                                QueueLimit = 0
                            }
                        )
                );
            }
        );

        var app = builder.Build();

        Logger = app.Logger;

        if (IsDevelopment)
            app.UseHttpLogging();
        else
            app.UseExceptionHandler("/error");

        app.UseForwardedHeaders();
        app.UseRateLimiter();
        app.UseWebSockets();
        MapEndpoints(app);
        app.Urls.Add(IsDevelopment ? "http://0.0.0.0:5000" : "https://0.0.0.0:5000");
        app.Run();
    }

    #region Web Application Initialization

    /// <summary>
    /// Reads <c>cert.pem</c> and <c>key.pem</c> from the working directory and configures
    /// Kestrel to terminate TLS with that certificate.
    /// </summary>
    /// <param name="builder">The web application builder to configure.</param>
    /// <returns>
    /// <see langword="true"/> if the certificate was loaded successfully;
    /// <see langword="false"/> if either file is missing or malformed (startup should abort).
    /// </returns>
    private static bool ConfigureHttpsCertificate(WebApplicationBuilder builder) {
        if (!File.Exists("cert.pem")) {
            Console.WriteLine("Certificate file 'cert.pem' does not exist");
            return false;
        }

        if (!File.Exists("key.pem")) {
            Console.WriteLine("Certificate key file 'key.pem' does not exist");
            return false;
        }

        string pem;
        string key;
        try {
            pem = File.ReadAllText("cert.pem");
            key = File.ReadAllText("key.pem");
        } catch (Exception e) {
            Console.WriteLine($"Could not read either 'cert.pem' or 'key.pem':\n{e}");
            return false;
        }

        X509Certificate2 x509;
        try {
            x509 = X509Certificate2.CreateFromPem(pem, key);
        } catch (CryptographicException e) {
            Console.WriteLine($"Could not create certificate object from pem files:\n{e}");
            return false;
        }

        builder.WebHost.ConfigureKestrel(s => { s.ListenAnyIP(5000, options => { options.UseHttps(x509); }); });

        return true;
    }

    #endregion

    #region Endpoint Registration

    /// <summary>
    /// Registers all HTTP and WebSocket endpoints.
    /// </summary>
    private static void MapEndpoints(WebApplication app) {
        var lobbyService = app.Services.GetRequiredService<LobbyService>();
        var joinService = app.Services.GetRequiredService<JoinSessionService>();

        // Health & Monitoring
        app.MapGet(
                "/",
                () => Results.Ok(
                    new {
                        service = "MMS",
                        version = MatchmakingProtocol.CurrentVersion,
                        status = "healthy"
                    }
                )
            )
            .WithName("HealthCheck");
        app.MapGet("/lobbies", GetLobbies).WithName("ListLobbies").RequireRateLimiting("search");

        // Lobby Management
        app.MapPost("/lobby", CreateLobby).WithName("CreateLobby").RequireRateLimiting("create");
        app.MapDelete("/lobby/{token}", CloseLobby).WithName("CloseLobby");

        // Host Operations
        app.MapPost("/lobby/heartbeat/{token}", Heartbeat).WithName("Heartbeat");
        app.MapPost("/lobby/discovery/verify/{token}", VerifyDiscovery).WithName("VerifyDiscovery");

        // Persistent host WebSocket for push notifications (refresh_host_mapping, start_punch).
        app.Map(
            "/ws/{token}", async (HttpContext context, string token) => {
                if (!context.WebSockets.IsWebSocketRequest) {
                    context.Response.StatusCode = 400;
                    return;
                }

                var lobby = lobbyService.GetLobbyByToken(token);
                if (lobby == null) {
                    context.Response.StatusCode = 404;
                    return;
                }

                using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                lobby.HostWebSocket = webSocket;

                Logger.LogInformation(
                    "[WS] Host connected for lobby {LobbyIdentifier}",
                    IsDevelopment ? lobby.ConnectionData : lobby.LobbyName
                );

                var buffer = new byte[1024];
                try {
                    while (webSocket.State == WebSocketState.Open) {
                        var result = await webSocket.ReceiveAsync(buffer, CancellationToken.None);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                    }
                } catch (WebSocketException) {
                    // Host disconnected without a proper close handshake (normal during game exit).
                } catch (Exception ex) when (ex.InnerException is System.Net.Sockets.SocketException) {
                    // Connection forcibly reset (normal during game exit).
                } finally {
                    lobby.HostWebSocket = null;
                    Logger.LogInformation(
                        "[WS] Host disconnected from lobby {LobbyIdentifier}",
                        IsDevelopment ? lobby.ConnectionData : lobby.LobbyName
                    );
                }
            }
        );

        // Short-lived client WebSocket used during the matchmaking rendezvous.
        app.Map(
            "/ws/join/{joinId}", async (HttpContext context, string joinId) => {
                if (!context.WebSockets.IsWebSocketRequest) {
                    context.Response.StatusCode = 400;
                    return;
                }

                if (!TryValidateMatchmakingVersion(context.Request.Query["matchmakingVersion"])) {
                    context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
                    await context.Response.WriteAsJsonAsync(
                        new ErrorResponse(
                            "Please update to the latest version in order to use matchmaking!",
                            MatchmakingProtocol.UpdateRequiredErrorCode
                        )
                    );
                    return;
                }

                var session = joinService.GetJoinSession(joinId);
                if (session == null) {
                    context.Response.StatusCode = 404;
                    return;
                }

                using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                if (!joinService.AttachJoinWebSocket(joinId, webSocket)) {
                    context.Response.StatusCode = 404;
                    return;
                }

                await joinService.SendBeginClientMappingAsync(joinId, context.RequestAborted);

                var lobby = lobbyService.GetLobby(session.LobbyConnectionData);
                if (lobby?.HostWebSocket is not { State: WebSocketState.Open }) {
                    await joinService.FailJoinSessionAsync(joinId, "host_unreachable", context.RequestAborted);
                    return;
                }

                if (lobby.ExternalPort == null) {
                    var refreshSent = await joinService.SendHostRefreshRequestAsync(joinId, context.RequestAborted);
                    if (!refreshSent) {
                        await joinService.FailJoinSessionAsync(joinId, "host_unreachable", context.RequestAborted);
                        return;
                    }
                }

                var buffer = new byte[256];
                try {
                    while (webSocket.State == WebSocketState.Open) {
                        var result = await webSocket.ReceiveAsync(buffer, context.RequestAborted);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                    }
                } catch (OperationCanceledException) {
                } catch (WebSocketException) {
                } finally {
                    if (joinService.GetJoinSession(joinId) != null)
                        await joinService.FailJoinSessionAsync(joinId, "client_disconnected", CancellationToken.None);
                }
            }
        );

        // Client Operations
        app.MapPost("/lobby/{connectionData}/join", JoinLobby).WithName("JoinLobby").RequireRateLimiting("join");
    }

    #endregion

    #region Endpoint Handlers

    /// <summary>
    /// Returns all lobbies, optionally filtered by type.
    /// </summary>
    private static IResult GetLobbies(LobbyService lobbyService, string? type = null, int? matchmakingVersion = null) {
        var lobbies = lobbyService.GetLobbies(type)
                                  .Select(l => new LobbyResponse(
                                          l.AdvertisedConnectionData,
                                          l.LobbyName,
                                          l.LobbyType,
                                          l.LobbyCode
                                      )
                                  );
        return TypedResults.Ok(lobbies);
    }

    /// <summary>
    /// Creates a new lobby (Steam or Matchmaking).
    /// </summary>
    private static IResult CreateLobby(
        CreateLobbyRequest request,
        LobbyService lobbyService,
        LobbyNameService lobbyNameService,
        HttpContext context
    ) {
        var lobbyType = request.LobbyType ?? "matchmaking";

        if (!string.Equals(lobbyType, "steam", StringComparison.OrdinalIgnoreCase) &&
            !ValidateMatchmakingVersion(request.MatchmakingVersion)) {
            return TypedResults.BadRequest(
                new ErrorResponse(
                    "Please update to the latest version in order to use matchmaking!",
                    MatchmakingProtocol.UpdateRequiredErrorCode
                )
            );
        }

        string connectionData;

        if (lobbyType == "steam") {
            if (string.IsNullOrEmpty(request.ConnectionData))
                return TypedResults.BadRequest(new ErrorResponse("Steam lobby requires ConnectionData"));

            connectionData = request.ConnectionData;
        } else {
            var rawHostIp = request.HostIp ?? context.Connection.RemoteIpAddress?.ToString();
            if (string.IsNullOrEmpty(rawHostIp) || !IPAddress.TryParse(rawHostIp, out var parsedHostIp))
                return TypedResults.BadRequest(new ErrorResponse("Invalid IP address"));

            if (request.HostPort is null or <= 0 or > 65535)
                return TypedResults.BadRequest(new ErrorResponse("Invalid port number"));

            connectionData = $"{parsedHostIp}:{request.HostPort}";
        }

        var lobbyName = lobbyNameService.GenerateLobbyName();
        var lobby = lobbyService.CreateLobby(
            connectionData,
            lobbyName,
            lobbyType,
            request.HostLanIp,
            request.IsPublic ?? true
        );

        var visibility = lobby.IsPublic ? "Public" : "Private";
        var connectionDataString = IsDevelopment ? lobby.AdvertisedConnectionData : "[Redacted]";
        Logger.LogInformation(
            "[LOBBY] Created: '{LobbyName}' [{LobbyType}] ({Visibility}) -> {ConnectionDataString} (Code: {LobbyCode})",
            lobby.LobbyName, lobby.LobbyType, visibility, connectionDataString, lobby.LobbyCode
        );

        return TypedResults.Created(
            $"/lobby/{lobby.LobbyCode}",
            new CreateLobbyResponse(
                lobby.AdvertisedConnectionData, lobby.HostToken, lobby.LobbyName, lobby.LobbyCode,
                lobby.HostDiscoveryToken
            )
        );
    }

    /// <summary>
    /// Returns the externally discovered port for a discovery token when available.
    /// </summary>
    /// <remarks>
    /// Retained for compatibility. The active matchmaking client flow uses the WebSocket
    /// rendezvous instead of polling this endpoint.
    /// </remarks>
    private static IResult VerifyDiscovery(
        string token,
        JoinSessionService joinService
    ) {
        var port = joinService.GetDiscoveredPort(token);
        return port is null
            ? TypedResults.Ok(new StatusResponse("pending"))
            : TypedResults.Ok(new DiscoveryResponse(port.Value));
    }

    /// <summary>
    /// Closes a lobby by host token.
    /// </summary>
    private static Results<NoContent, NotFound<ErrorResponse>> CloseLobby(
        string token,
        LobbyService lobbyService,
        JoinSessionService joinService
    ) {
        if (!lobbyService.RemoveLobbyByToken(token, joinService.CleanupSessionsForLobby))
            return TypedResults.NotFound(new ErrorResponse("Lobby not found"));

        Logger.LogInformation("[LOBBY] Closed by host");
        return TypedResults.NoContent();
    }

    /// <summary>
    /// Refreshes the lobby heartbeat to prevent expiration.
    /// </summary>
    private static Results<Ok<StatusResponse>, NotFound<ErrorResponse>> Heartbeat(
        string token,
        HeartbeatRequest request,
        LobbyService lobbyService
    ) {
        return lobbyService.Heartbeat(token, request.ConnectedPlayers)
            ? TypedResults.Ok(new StatusResponse("alive"))
            : TypedResults.NotFound(new ErrorResponse("Lobby not found"));
    }

    /// <summary>
    /// Registers a client join attempt, returning host connection info and a discovery token
    /// for the subsequent WebSocket rendezvous.
    /// </summary>
    private static IResult JoinLobby(
        string connectionData,
        JoinLobbyRequest request,
        LobbyService lobbyService,
        JoinSessionService joinService,
        HttpContext context
    ) {
        // Accept either a lobby code or raw connection data.
        var lobby = lobbyService.GetLobbyByCode(connectionData) ?? lobbyService.GetLobby(connectionData);
        if (lobby == null)
            return TypedResults.NotFound(new ErrorResponse("Lobby not found"));

        if (string.Equals(lobby.LobbyType, "matchmaking", StringComparison.OrdinalIgnoreCase) &&
            !ValidateMatchmakingVersion(request.MatchmakingVersion)) {
            return TypedResults.BadRequest(
                new ErrorResponse(
                    "Please update to the latest version in order to use matchmaking!",
                    MatchmakingProtocol.UpdateRequiredErrorCode
                )
            );
        }

        var rawClientIp = request.ClientIp ?? context.Connection.RemoteIpAddress?.ToString();
        if (string.IsNullOrEmpty(rawClientIp) || !IPAddress.TryParse(rawClientIp, out var parsedIp))
            return TypedResults.NotFound(new ErrorResponse("Invalid IP address"));

        if (request.ClientPort is <= 0 or > 65535)
            return TypedResults.NotFound(new ErrorResponse("Invalid port"));

        var clientIp = parsedIp.ToString();

        Logger.LogInformation(
            "[JOIN] {ConnectionDetails}",
            IsDevelopment
                ? $"{clientIp}:{request.ClientPort} -> {lobby.AdvertisedConnectionData}"
                : $"[Redacted]:{request.ClientPort} -> [Redacted]"
        );

        string? lanConnectionData = null;
        if (!string.IsNullOrEmpty(lobby.HostLanIp) && clientIp == lobby.ConnectionData.Split(':')[0]) {
            Logger.LogInformation("[JOIN] Local Network Detected! Returning LAN IP: {HostLanIp}", lobby.HostLanIp);
            lanConnectionData = lobby.HostLanIp;
        }

        if (string.Equals(lobby.LobbyType, "matchmaking", StringComparison.OrdinalIgnoreCase)) {
            var session = joinService.CreateJoinSession(lobby, clientIp);
            if (session == null)
                return TypedResults.NotFound(new ErrorResponse("Lobby not found"));

            return TypedResults.Ok(
                new JoinResponse(
                    lobby.AdvertisedConnectionData,
                    lobby.LobbyType,
                    clientIp,
                    request.ClientPort,
                    lanConnectionData,
                    session.ClientDiscoveryToken,
                    session.JoinId
                )
            );
        }

        return TypedResults.Ok(
            new JoinResponse(
                lobby.AdvertisedConnectionData,
                lobby.LobbyType,
                clientIp,
                request.ClientPort,
                lanConnectionData,
                null,
                null
            )
        );
    }

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="matchmakingVersion"/> matches the current protocol version.
    /// </summary>
    private static bool ValidateMatchmakingVersion(int? matchmakingVersion) =>
        matchmakingVersion == MatchmakingProtocol.CurrentVersion;

    /// <summary>
    /// Parses and validates a matchmaking version from a query string value.
    /// </summary>
    /// <param name="matchmakingVersion">Raw query string value.</param>
    /// <returns><see langword="true"/> if the version is present and matches the current protocol.</returns>
    private static bool TryValidateMatchmakingVersion(string? matchmakingVersion) {
        return int.TryParse(matchmakingVersion, out var parsedVersion) &&
               parsedVersion == MatchmakingProtocol.CurrentVersion;
    }

    #endregion

    #region DTOs

    /// <param name="HostIp">Host IP address (matchmaking only, optional - defaults to connection IP).</param>
    /// <param name="HostPort">Host UDP port (matchmaking only).</param>
    /// <param name="ConnectionData">Steam lobby ID (Steam only).</param>
    /// <param name="LobbyType"><c>"steam"</c> or <c>"matchmaking"</c> (default: <c>"matchmaking"</c>).</param>
    /// <param name="HostLanIp">Host LAN address for same-network fast-path discovery.</param>
    /// <param name="IsPublic">Whether the lobby appears in public browser listings (default: <see langword="true"/>).</param>
    /// <param name="MatchmakingVersion">Client matchmaking protocol version for compatibility checks.</param>
    [UsedImplicitly]
    private record CreateLobbyRequest(
        string? HostIp,
        int? HostPort,
        string? ConnectionData,
        string? LobbyType,
        string? HostLanIp,
        bool? IsPublic,
        int? MatchmakingVersion
    );

    /// <param name="ConnectionData">Connection identifier (<c>IP:Port</c> or Steam lobby ID).</param>
    /// <param name="HostToken">Secret token required for host operations (heartbeat, close).</param>
    /// <param name="LobbyName">Display name assigned to the lobby.</param>
    /// <param name="LobbyCode">Human-readable invite code (e.g. <c>ABC123</c>).</param>
    /// <param name="HostDiscoveryToken">Token the host sends via UDP so MMS can map its external port.</param>
    [UsedImplicitly]
    internal record CreateLobbyResponse(
        string ConnectionData,
        string HostToken,
        string LobbyName,
        string LobbyCode,
        string? HostDiscoveryToken
    );

    /// <param name="ConnectionData">Connection identifier (<c>IP:Port</c> or Steam lobby ID).</param>
    /// <param name="Name">Display name.</param>
    /// <param name="LobbyType"><c>"steam"</c> or <c>"matchmaking"</c>.</param>
    /// <param name="LobbyCode">Human-readable invite code.</param>
    [UsedImplicitly]
    internal record LobbyResponse(
        string ConnectionData,
        string Name,
        string LobbyType,
        string LobbyCode
    );

    /// <param name="ClientIp">Client IP override (optional - defaults to the connection's remote IP).</param>
    /// <param name="ClientPort">Client UDP port for NAT hole-punching.</param>
    /// <param name="MatchmakingVersion">Client matchmaking protocol version for compatibility checks.</param>
    [UsedImplicitly]
    internal record JoinLobbyRequest(string? ClientIp, int ClientPort, int? MatchmakingVersion);

    /// <param name="ConnectionData">Host connection data (<c>IP:Port</c> or Steam lobby ID).</param>
    /// <param name="LobbyType"><c>"steam"</c> or <c>"matchmaking"</c>.</param>
    /// <param name="ClientIp">Client public IP as observed by MMS.</param>
    /// <param name="ClientPort">Client public port as observed by MMS.</param>
    /// <param name="LanConnectionData">Host LAN address returned when client and host share a network.</param>
    /// <param name="ClientDiscoveryToken">Token the client sends via UDP so MMS can map its external port.</param>
    /// <param name="JoinId">Identifier for the WebSocket rendezvous session.</param>
    [UsedImplicitly]
    internal record JoinResponse(
        string ConnectionData,
        string LobbyType,
        string ClientIp,
        int ClientPort,
        string? LanConnectionData,
        string? ClientDiscoveryToken,
        string? JoinId
    );

    /// <param name="ExternalPort">The external UDP port discovered for the token sender.</param>
    [UsedImplicitly]
    internal record DiscoveryResponse(int ExternalPort);

    /// <param name="Error">Human-readable error description.</param>
    /// <param name="ErrorCode">Optional machine-readable error code (e.g. <c>"update_required"</c>).</param>
    [UsedImplicitly]
    internal record ErrorResponse(string Error, string? ErrorCode = null);

    /// <param name="Status">Short status string (e.g. <c>"alive"</c>, <c>"pending"</c>).</param>
    [UsedImplicitly]
    internal record StatusResponse(string Status);

    /// <param name="ConnectedPlayers">Number of remote players currently connected to the host.</param>
    [UsedImplicitly]
    internal record HeartbeatRequest(int ConnectedPlayers);

    #endregion
}
