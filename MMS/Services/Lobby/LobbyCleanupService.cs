namespace MMS.Services.Lobby;

using Matchmaking;

/// <summary>Background service that removes expired lobbies and matchmaking sessions every 30 seconds.</summary>
public class LobbyCleanupService(LobbyService lobbyService, JoinSessionService joinSessionService) : BackgroundService {
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        Console.WriteLine("[CLEANUP] Service started");

        while (!stoppingToken.IsCancellationRequested) {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

            var removed = lobbyService.CleanupDeadLobbies(joinSessionService.CleanupSessionsForLobby);
            joinSessionService.CleanupExpiredSessions();
            if (removed > 0) {
                Console.WriteLine($"[CLEANUP] Removed {removed} expired lobbies");
            }
        }
    }
}
