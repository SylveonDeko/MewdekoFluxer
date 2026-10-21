namespace Mewdeko.Modules.Games.Common.Kaladont;

/// <summary>
///     Represents a player in a Kaladont game.
/// </summary>
/// <param name="UserId">The Fluxer user ID of the player.</param>
/// <param name="UserName">The Fluxer username of the player.</param>
public record KaladontPlayer(ulong UserId, string UserName);