using System.Collections.Generic;
using SSMP.Api.Command;
using SSMP.Api.Command.Server;
using SSMP.Game.Server;

namespace SSMP.Game.Command.Server;

/// <summary>
/// Command for admins to toggle spying on private messages.
/// </summary>
internal class SpyCommand : IServerCommand, ICommandWithDescription {

    /// <inheritdoc />
    public string Trigger => "/spy";

    /// <inheritdoc />
    public string[] Aliases => [];

    /// <inheritdoc />
    public string Description => "Toggle spying on private messages between players.";

    /// <inheritdoc />
    public bool AuthorizedOnly => true;

    /// <summary>
    /// Reference to the spy set managed by ServerManager.
    /// </summary>
    private readonly HashSet<ushort> _spyingPlayers;


    public SpyCommand(HashSet<ushort> spyingPlayers) {
        _spyingPlayers = spyingPlayers;
    }
    
    /// <inheritdoc />
    public void Execute(ICommandSender commandSender, string[] args) {
        if (commandSender is not PlayerCommandSender playerSender) {
            commandSender.SendMessage("This command can only be used by players.");
            return;
        }

        var playerId = playerSender.Id;

        if (!_spyingPlayers.Add(playerId)) {
            _spyingPlayers.Remove(playerId);
            commandSender.SendMessage("Spy mode: OFF. You will no longer see private messages.");
        } else {
            commandSender.SendMessage("Spy mode: ON. You will now see all private messages.");
        }
    }
}
